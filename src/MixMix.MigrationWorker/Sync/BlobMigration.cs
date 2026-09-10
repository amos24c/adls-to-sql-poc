using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MixMix.MigrationWorker.Configuration;
using MixMix.MigrationWorker.Databricks;
using MixMix.MigrationWorker.Models;
using MixMix.MigrationWorker.Parquet;
using MixMix.MigrationWorker.Sql;

namespace MixMix.MigrationWorker.Sync;

/// <summary>Loads dbo.Blob, the other parent of dbo.ItemBlob. Source and target schemas match.</summary>
public sealed class BlobMigration(
    IDatabricksFilesClient filesClient,
    ISqlConnectionFactory connectionFactory,
    IOptions<MigrationOptions> options,
    ILogger<BlobMigration> logger)
    : TableMigration<BlobRecord>(filesClient, connectionFactory, options.Value, logger)
{
    private const int NameMaxLength = 50;

    private static readonly string[] BlobIdColumns = ["BlobId", "BlobID", "Id"];
    private static readonly string[] NameColumns = ["Name"];

    public override string TableName => "Blob";

    protected override string StagingTableName => "#stage_Blob";

    protected override string CreateStagingTableSql => """
        IF OBJECT_ID(N'tempdb..#stage_Blob') IS NOT NULL DROP TABLE #stage_Blob;

        CREATE TABLE #stage_Blob
        (
            BlobId int         NOT NULL,
            Name   varchar(50) NOT NULL
        );
        """;

    protected override string MergeSql => """
        SET NOCOUNT ON;

        DECLARE @staged bigint = (SELECT COUNT_BIG(*) FROM #stage_Blob);

        IF OBJECT_ID(N'tempdb..#src_Blob') IS NOT NULL DROP TABLE #src_Blob;

        SELECT s.BlobId, s.Name
        INTO #src_Blob
        FROM (
            SELECT BlobId,
                   Name,
                   ROW_NUMBER() OVER (PARTITION BY BlobId ORDER BY (SELECT NULL)) AS DuplicateRank
            FROM #stage_Blob
        ) AS s
        WHERE s.DuplicateRank = 1;

        DECLARE @eligible bigint = (SELECT COUNT_BIG(*) FROM #src_Blob);
        DECLARE @actions TABLE (MergeAction nvarchar(10));

        MERGE dbo.Blob WITH (HOLDLOCK) AS t
        USING #src_Blob AS s
            ON t.BlobId = s.BlobId
        WHEN MATCHED AND EXISTS (SELECT s.Name EXCEPT SELECT t.Name)
            THEN UPDATE SET t.Name = s.Name
        WHEN NOT MATCHED BY TARGET
            THEN INSERT (BlobId, Name) VALUES (s.BlobId, s.Name)
        OUTPUT $action INTO @actions;

        SELECT Inserted = COUNT_BIG(CASE WHEN MergeAction = 'INSERT' THEN 1 END),
               Updated  = COUNT_BIG(CASE WHEN MergeAction = 'UPDATE' THEN 1 END),
               Rejected = @staged - @eligible
        FROM @actions;
        """;

    protected override IReadOnlyList<StagingColumn<BlobRecord>> Columns { get; } =
    [
        new("BlobId", typeof(int), record => record.BlobId),
        new("Name", typeof(string), record => record.Name)
    ];

    protected override IReadOnlyCollection<string> SourceColumns { get; } = [.. BlobIdColumns, .. NameColumns];

    protected internal override bool TryMapRow(ParquetBatch batch, int row, MappingStats stats, out BlobRecord record)
    {
        record = null!;

        var blobId = batch.GetInt32(row, BlobIdColumns);
        var name = TextValue.Fit(batch.GetStringOrNull(row, NameColumns), NameMaxLength, stats);

        // dbo.Blob.Name is NOT NULL in the target, so a null would abort the whole bulk copy.
        if (name is null)
        {
            stats.Filtered++;
            Logger.LogWarning("Skipping Blob {BlobId} because its Name is null and dbo.Blob.Name is NOT NULL.", blobId);
            return false;
        }

        record = new BlobRecord(blobId, name);
        return true;
    }
}
