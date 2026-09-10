using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MixMix.MigrationWorker.Configuration;
using MixMix.MigrationWorker.Databricks;
using MixMix.MigrationWorker.Models;
using MixMix.MigrationWorker.Parquet;
using MixMix.MigrationWorker.Sql;

namespace MixMix.MigrationWorker.Sync;

/// <summary>
/// Loads dbo.ItemCategory, the parent that dbo.Item.CategoryId points at, from the staged dbo.Category extract.
/// The target names the label column Descr while the source calls it Name, and dbo.ItemCategory.Id is an
/// IDENTITY column, so source ids are carried over explicitly to keep dbo.Item.CategoryId resolvable.
/// </summary>
public sealed class ItemCategoryMigration(
    IDatabricksFilesClient filesClient,
    ISqlConnectionFactory connectionFactory,
    IOptions<MigrationOptions> options,
    ILogger<ItemCategoryMigration> logger)
    : TableMigration<ItemCategoryRecord>(filesClient, connectionFactory, options.Value, logger)
{
    private const int DescrMaxLength = 100;

    private static readonly string[] IdColumns = ["Id", "CategoryId", "CategoryID", "ItemCategoryId"];
    private static readonly string[] DescrColumns = ["Descr", "Name", "DisplayName"];

    public override string TableName => "ItemCategory";

    protected override string StagingTableName => "#stage_ItemCategory";

    protected override string CreateStagingTableSql => """
        IF OBJECT_ID(N'tempdb..#stage_ItemCategory') IS NOT NULL DROP TABLE #stage_ItemCategory;

        CREATE TABLE #stage_ItemCategory
        (
            Id    int           NOT NULL,
            Descr nvarchar(100) NULL
        );
        """;

    protected override string MergeSql => """
        SET NOCOUNT ON;

        DECLARE @staged bigint = (SELECT COUNT_BIG(*) FROM #stage_ItemCategory);

        IF OBJECT_ID(N'tempdb..#src_ItemCategory') IS NOT NULL DROP TABLE #src_ItemCategory;

        SELECT s.Id, s.Descr
        INTO #src_ItemCategory
        FROM (
            SELECT Id,
                   Descr,
                   ROW_NUMBER() OVER (PARTITION BY Id ORDER BY (SELECT NULL)) AS DuplicateRank
            FROM #stage_ItemCategory
        ) AS s
        WHERE s.DuplicateRank = 1;

        DECLARE @eligible bigint = (SELECT COUNT_BIG(*) FROM #src_ItemCategory);
        DECLARE @actions TABLE (MergeAction nvarchar(10));

        SET IDENTITY_INSERT dbo.ItemCategory ON;

        MERGE dbo.ItemCategory WITH (HOLDLOCK) AS t
        USING #src_ItemCategory AS s
            ON t.Id = s.Id
        WHEN MATCHED AND EXISTS (SELECT s.Descr EXCEPT SELECT t.Descr)
            THEN UPDATE SET t.Descr = s.Descr
        WHEN NOT MATCHED BY TARGET
            THEN INSERT (Id, Descr) VALUES (s.Id, s.Descr)
        OUTPUT $action INTO @actions;

        SET IDENTITY_INSERT dbo.ItemCategory OFF;

        SELECT Inserted = COUNT_BIG(CASE WHEN MergeAction = 'INSERT' THEN 1 END),
               Updated  = COUNT_BIG(CASE WHEN MergeAction = 'UPDATE' THEN 1 END),
               Rejected = @staged - @eligible
        FROM @actions;
        """;

    protected override string? PostMergeSql => Options.ReseedIdentityColumns
        ? """
          DECLARE @maxCategoryId int = (SELECT ISNULL(MAX(Id), 0) FROM dbo.ItemCategory);
          DBCC CHECKIDENT ('dbo.ItemCategory', RESEED, @maxCategoryId) WITH NO_INFOMSGS;
          """
        : null;

    protected override IReadOnlyList<StagingColumn<ItemCategoryRecord>> Columns { get; } =
    [
        new("Id", typeof(int), record => record.Id),
        new("Descr", typeof(string), record => record.Descr)
    ];

    protected override IReadOnlyCollection<string> SourceColumns { get; } = [.. IdColumns, .. DescrColumns];

    protected internal override bool TryMapRow(
        ParquetBatch batch,
        int row,
        MappingStats stats,
        out ItemCategoryRecord record)
    {
        var id = batch.GetInt32(row, IdColumns);
        var descr = TextValue.Fit(batch.GetStringOrNull(row, DescrColumns), DescrMaxLength, stats);

        // Categories are loaded regardless of their Active/Deleted flags: dropping one would orphan every
        // item that points at it, and the target has no column to carry the flag anyway.
        record = new ItemCategoryRecord(id, descr);
        return true;
    }
}
