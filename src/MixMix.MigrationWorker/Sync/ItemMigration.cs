using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MixMix.MigrationWorker.Configuration;
using MixMix.MigrationWorker.Databricks;
using MixMix.MigrationWorker.Models;
using MixMix.MigrationWorker.Parquet;
using MixMix.MigrationWorker.Sql;

namespace MixMix.MigrationWorker.Sync;

/// <summary>
/// Loads dbo.Item from the staged dbo.ItemNew extract, keeping only the three target columns.
/// Source ItemIDs are carried over verbatim under IDENTITY_INSERT: dbo.ItemBlob.ItemId references them,
/// so letting the target generate new identity values would break the relationship.
/// </summary>
public sealed class ItemMigration(
    IDatabricksFilesClient filesClient,
    ISqlConnectionFactory connectionFactory,
    IOptions<MigrationOptions> options,
    ILogger<ItemMigration> logger)
    : TableMigration<ItemRecord>(filesClient, connectionFactory, options.Value, logger)
{
    private const int NameMaxLength = 100;

    private static readonly string[] IdColumns = ["ItemID", "ItemId", "Id"];
    private static readonly string[] CategoryIdColumns = ["CategoryId", "CategoryID"];
    private static readonly string[] NameColumns = ["Name"];
    private static readonly string[] DeletedColumns = ["Deleted"];
    private static readonly string[] ActiveColumns = ["Active"];

    public override string TableName => "Item";

    protected override string StagingTableName => "#stage_Item";

    protected override string CreateStagingTableSql => """
        IF OBJECT_ID(N'tempdb..#stage_Item') IS NOT NULL DROP TABLE #stage_Item;

        CREATE TABLE #stage_Item
        (
            Id         int           NOT NULL,
            CategoryId int           NOT NULL,
            Name       nvarchar(100) NULL
        );
        """;

    protected override string MergeSql => """
        SET NOCOUNT ON;

        DECLARE @staged bigint = (SELECT COUNT_BIG(*) FROM #stage_Item);

        IF OBJECT_ID(N'tempdb..#src_Item') IS NOT NULL DROP TABLE #src_Item;

        -- Drop duplicate ids and any row whose category is absent, so the FK to dbo.ItemCategory cannot fire.
        SELECT s.Id, s.CategoryId, s.Name
        INTO #src_Item
        FROM (
            SELECT Id,
                   CategoryId,
                   Name,
                   ROW_NUMBER() OVER (PARTITION BY Id ORDER BY (SELECT NULL)) AS DuplicateRank
            FROM #stage_Item
        ) AS s
        WHERE s.DuplicateRank = 1
          AND EXISTS (SELECT 1 FROM dbo.ItemCategory AS c WHERE c.Id = s.CategoryId);

        DECLARE @eligible bigint = (SELECT COUNT_BIG(*) FROM #src_Item);
        DECLARE @actions TABLE (MergeAction nvarchar(10));

        SET IDENTITY_INSERT dbo.Item ON;

        MERGE dbo.Item WITH (HOLDLOCK) AS t
        USING #src_Item AS s
            ON t.Id = s.Id
        WHEN MATCHED AND EXISTS (SELECT s.CategoryId, s.Name EXCEPT SELECT t.CategoryId, t.Name)
            THEN UPDATE SET t.CategoryId = s.CategoryId,
                            t.Name = s.Name
        WHEN NOT MATCHED BY TARGET
            THEN INSERT (Id, CategoryId, Name) VALUES (s.Id, s.CategoryId, s.Name)
        OUTPUT $action INTO @actions;

        SET IDENTITY_INSERT dbo.Item OFF;

        SELECT Inserted = COUNT_BIG(CASE WHEN MergeAction = 'INSERT' THEN 1 END),
               Updated  = COUNT_BIG(CASE WHEN MergeAction = 'UPDATE' THEN 1 END),
               Rejected = @staged - @eligible
        FROM @actions;
        """;

    /// <summary>
    /// Explicit id inserts leave the identity seed behind the highest loaded id, which would collide the
    /// first time the application inserts an item of its own.
    /// </summary>
    /// <summary>
    /// dbo.ItemBlob rows are removed first, because they reference the items being deleted. Any of them still
    /// present in the itemblob extract are then reported as rejected by that step, since their item is gone.
    /// </summary>
    protected internal override string? PruneSql => """
        SET NOCOUNT ON;

        DELETE b
        FROM dbo.ItemBlob AS b
        WHERE NOT EXISTS (SELECT 1 FROM #stage_Item AS s WHERE s.Id = b.ItemId);

        DELETE t
        FROM dbo.Item AS t
        WHERE NOT EXISTS (SELECT 1 FROM #stage_Item AS s WHERE s.Id = t.Id);

        SELECT Deleted = CAST(@@ROWCOUNT AS bigint);
        """;

    /// <summary>
    /// The seed is set explicitly rather than with a bare RESEED, which only ever raises it: after a prune
    /// removes the highest item, a bare RESEED would leave the seed stranded above the surviving rows.
    /// </summary>
    protected override string? PostMergeSql => Options.ReseedIdentityColumns
        ? """
          DECLARE @maxItemId int = (SELECT ISNULL(MAX(Id), 0) FROM dbo.Item);
          DBCC CHECKIDENT ('dbo.Item', RESEED, @maxItemId) WITH NO_INFOMSGS;
          """
        : null;

    protected override IReadOnlyList<StagingColumn<ItemRecord>> Columns { get; } =
    [
        new("Id", typeof(int), record => record.Id),
        new("CategoryId", typeof(int), record => record.CategoryId),
        new("Name", typeof(string), record => record.Name)
    ];

    protected override IReadOnlyCollection<string> SourceColumns { get; } =
    [
        .. IdColumns,
        .. CategoryIdColumns,
        .. NameColumns,
        .. DeletedColumns,
        .. ActiveColumns
    ];

    /// <summary>
    /// A filter whose source column was never staged silently passes every row through, which for
    /// ExcludeDeletedItems means quietly migrating rows the source considers deleted.
    /// </summary>
    protected internal override void InspectSchema(ParquetBatch batch)
    {
        WarnIfFilterColumnMissing(batch, Options.ExcludeDeletedItems, DeletedColumns[0], nameof(Options.ExcludeDeletedItems));
        WarnIfFilterColumnMissing(batch, Options.ExcludeInactiveItems, ActiveColumns[0], nameof(Options.ExcludeInactiveItems));
    }

    private void WarnIfFilterColumnMissing(ParquetBatch batch, bool enabled, string column, string optionName)
    {
        if (!enabled || batch.HasColumn(column))
        {
            return;
        }

        Logger.LogWarning(
            "{OptionName} is enabled but the staged extract has no {Column} column, so no rows will be "
            + "filtered on it. Add {Column} to the export job's column list, or turn the option off so the "
            + "intent is explicit. Columns staged for dbo.Item: [{StagedColumns}].",
            optionName,
            column,
            column,
            string.Join(", ", batch.ColumnNames));
    }

    protected internal override bool TryMapRow(ParquetBatch batch, int row, MappingStats stats, out ItemRecord record)
    {
        record = null!;

        if (Options.ExcludeDeletedItems && batch.GetBooleanOrNull(row, DeletedColumns) == true)
        {
            stats.Filtered++;
            return false;
        }

        if (Options.ExcludeInactiveItems && batch.GetBooleanOrNull(row, ActiveColumns) == false)
        {
            stats.Filtered++;
            return false;
        }

        var id = batch.GetInt32(row, IdColumns);
        var categoryId = batch.GetInt32(row, CategoryIdColumns);
        var name = TextValue.Fit(batch.GetStringOrNull(row, NameColumns), NameMaxLength, stats);

        record = new ItemRecord(id, categoryId, name);
        return true;
    }
}
