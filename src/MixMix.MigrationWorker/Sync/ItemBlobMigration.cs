using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MixMix.MigrationWorker.Configuration;
using MixMix.MigrationWorker.Databricks;
using MixMix.MigrationWorker.Models;
using MixMix.MigrationWorker.Parquet;
using MixMix.MigrationWorker.Sql;

namespace MixMix.MigrationWorker.Sync;

/// <summary>
/// Loads dbo.ItemBlob last, since it depends on both dbo.Item and dbo.Blob. Rows whose parents did not
/// survive the earlier steps are dropped rather than allowed to fail the batch on a FK violation.
/// </summary>
public sealed class ItemBlobMigration(
    IDatabricksFilesClient filesClient,
    ISqlConnectionFactory connectionFactory,
    IOptions<MigrationOptions> options,
    ILogger<ItemBlobMigration> logger)
    : TableMigration<ItemBlobRecord>(filesClient, connectionFactory, options.Value, logger)
{
    private static readonly string[] ItemBlobIdColumns = ["ItemBlobId", "ItemBlobID"];
    private static readonly string[] ItemIdColumns = ["ItemId", "ItemID"];
    private static readonly string[] BlobIdColumns = ["BlobId", "BlobID"];

    public override string TableName => "ItemBlob";

    protected override string StagingTableName => "#stage_ItemBlob";

    protected override string CreateStagingTableSql => """
        IF OBJECT_ID(N'tempdb..#stage_ItemBlob') IS NOT NULL DROP TABLE #stage_ItemBlob;

        CREATE TABLE #stage_ItemBlob
        (
            ItemBlobId int NOT NULL,
            ItemId     int NOT NULL,
            BlobId     int NOT NULL
        );
        """;

    protected override string MergeSql => """
        SET NOCOUNT ON;

        DECLARE @staged bigint = (SELECT COUNT_BIG(*) FROM #stage_ItemBlob);

        IF OBJECT_ID(N'tempdb..#src_ItemBlob') IS NOT NULL DROP TABLE #src_ItemBlob;

        SELECT s.ItemBlobId, s.ItemId, s.BlobId
        INTO #src_ItemBlob
        FROM (
            SELECT ItemBlobId,
                   ItemId,
                   BlobId,
                   ROW_NUMBER() OVER (PARTITION BY ItemBlobId ORDER BY (SELECT NULL)) AS DuplicateRank
            FROM #stage_ItemBlob
        ) AS s
        WHERE s.DuplicateRank = 1
          AND EXISTS (SELECT 1 FROM dbo.Item AS i WHERE i.Id = s.ItemId)
          AND EXISTS (SELECT 1 FROM dbo.Blob AS b WHERE b.BlobId = s.BlobId);

        DECLARE @eligible bigint = (SELECT COUNT_BIG(*) FROM #src_ItemBlob);
        DECLARE @actions TABLE (MergeAction nvarchar(10));

        MERGE dbo.ItemBlob WITH (HOLDLOCK) AS t
        USING #src_ItemBlob AS s
            ON t.ItemBlobId = s.ItemBlobId
        WHEN MATCHED AND EXISTS (SELECT s.ItemId, s.BlobId EXCEPT SELECT t.ItemId, t.BlobId)
            THEN UPDATE SET t.ItemId = s.ItemId,
                            t.BlobId = s.BlobId
        WHEN NOT MATCHED BY TARGET
            THEN INSERT (ItemBlobId, ItemId, BlobId) VALUES (s.ItemBlobId, s.ItemId, s.BlobId)
        OUTPUT $action INTO @actions;

        SELECT Inserted = COUNT_BIG(CASE WHEN MergeAction = 'INSERT' THEN 1 END),
               Updated  = COUNT_BIG(CASE WHEN MergeAction = 'UPDATE' THEN 1 END),
               Rejected = @staged - @eligible
        FROM @actions;
        """;

    protected internal override string? PruneSql => """
        SET NOCOUNT ON;

        DELETE t
        FROM dbo.ItemBlob AS t
        WHERE NOT EXISTS (SELECT 1 FROM #stage_ItemBlob AS s WHERE s.ItemBlobId = t.ItemBlobId);

        SELECT Deleted = CAST(@@ROWCOUNT AS bigint);
        """;

    protected override IReadOnlyList<StagingColumn<ItemBlobRecord>> Columns { get; } =
    [
        new("ItemBlobId", typeof(int), record => record.ItemBlobId),
        new("ItemId", typeof(int), record => record.ItemId),
        new("BlobId", typeof(int), record => record.BlobId)
    ];

    protected override IReadOnlyCollection<string> SourceColumns { get; } =
        [.. ItemBlobIdColumns, .. ItemIdColumns, .. BlobIdColumns];

    protected internal override bool TryMapRow(ParquetBatch batch, int row, MappingStats stats, out ItemBlobRecord record)
    {
        var itemBlobId = batch.GetInt32(row, ItemBlobIdColumns);
        var itemId = batch.GetInt32(row, ItemIdColumns);
        var blobId = batch.GetInt32(row, BlobIdColumns);

        record = new ItemBlobRecord(itemBlobId, itemId, blobId);
        return true;
    }
}
