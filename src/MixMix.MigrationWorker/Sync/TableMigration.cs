using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using MixMix.MigrationWorker.Configuration;
using MixMix.MigrationWorker.Databricks;
using MixMix.MigrationWorker.Parquet;
using MixMix.MigrationWorker.Sql;

namespace MixMix.MigrationWorker.Sync;

/// <summary>
/// Shared load pipeline: walk the staged folder, stream each Parquet row group through the row mapper,
/// bulk copy into a session-scoped staging table, then merge once into the target table.
/// </summary>
public abstract class TableMigration<TRecord>(
    IDatabricksFilesClient filesClient,
    ISqlConnectionFactory connectionFactory,
    MigrationOptions options,
    ILogger logger) : ITableMigration
{
    private bool _schemaInspected;

    public abstract string TableName { get; }

    /// <summary>Session-scoped staging table, for example #stage_Item.</summary>
    protected abstract string StagingTableName { get; }

    protected abstract string CreateStagingTableSql { get; }

    /// <summary>Merges the staging table into the target and returns one row: Inserted, Updated, Rejected.</summary>
    protected abstract string MergeSql { get; }

    protected abstract IReadOnlyList<StagingColumn<TRecord>> Columns { get; }

    /// <summary>
    /// Every source column name (including accepted aliases) that <see cref="TryMapRow"/> reads.
    /// Anything not listed here is never decoded out of the Parquet file.
    /// </summary>
    protected abstract IReadOnlyCollection<string> SourceColumns { get; }

    /// <summary>Runs on the same connection after a successful merge. Return null when nothing is needed.</summary>
    protected virtual string? PostMergeSql => null;

    /// <summary>
    /// Deletes target rows absent from the staging table and returns one row: Deleted. Only runs when
    /// <see cref="MigrationOptions.PruneMissingRows"/> is on, and only for tables that implement it.
    /// Compare against the staging table rather than the merge's eligible set, so a row held back for a
    /// missing parent is not mistaken for a row the source no longer has.
    /// </summary>
    protected internal virtual string? PruneSql => null;

    protected MigrationOptions Options => options;

    protected ILogger Logger => logger;

    /// <summary>Maps one Parquet row. Return false to drop the row and count it as filtered.</summary>
    protected internal abstract bool TryMapRow(ParquetBatch batch, int row, MappingStats stats, out TRecord record);

    /// <summary>
    /// Called once per run with the first row group, to report anything about the staged schema that would
    /// otherwise change behaviour silently.
    /// </summary>
    protected internal virtual void InspectSchema(ParquetBatch batch)
    {
    }

    public async Task<TableMigrationResult> ExecuteAsync(
        MigrationRunContext context,
        CancellationToken cancellationToken)
    {
        var result = new TableMigrationResult(TableName);
        var folderPath = $"{context.RunPath.TrimEnd('/')}/{options.GetSourceFolder(TableName)}";

        var files = await ListParquetFilesAsync(folderPath, cancellationToken).ConfigureAwait(false);

        if (files.Count == 0)
        {
            if (!options.AllowEmptySourceFolders)
            {
                throw new EmptySourceFolderException(
                    $"No Parquet files were found under '{folderPath}', so dbo.{TableName} was not loaded. "
                    + "Confirm the export job finished writing this run, or set "
                    + $"{MigrationOptions.SectionName}:{nameof(MigrationOptions.AllowEmptySourceFolders)} "
                    + "to true if the table is genuinely empty.");
            }

            logger.LogWarning(
                "No Parquet files found under {FolderPath}; leaving dbo.{TableName} untouched.",
                folderPath,
                TableName);
            return result;
        }

        logger.LogInformation(
            "Loading dbo.{TableName} from {FileCount} Parquet file(s) under {FolderPath}.",
            TableName,
            files.Count,
            folderPath);

        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await ExecuteNonQueryAsync(connection, CreateStagingTableSql, cancellationToken).ConfigureAwait(false);

        using var bulkCopy = CreateBulkCopy(connection);
        var buffer = new List<TRecord>(options.BatchSize);

        foreach (var file in files)
        {
            await StageFileAsync(file, bulkCopy, buffer, result, cancellationToken).ConfigureAwait(false);
            result.FilesProcessed++;
        }

        await FlushAsync(bulkCopy, buffer, result, cancellationToken).ConfigureAwait(false);
        await MergeAsync(connection, result, cancellationToken).ConfigureAwait(false);
        await PruneAsync(connection, result, cancellationToken).ConfigureAwait(false);

        // Reseeding comes last, because pruning can lower the highest surviving id.
        if (PostMergeSql is { } postMergeSql)
        {
            await ExecuteNonQueryAsync(connection, postMergeSql, cancellationToken).ConfigureAwait(false);
        }

        return result;
    }

    private async Task StageFileAsync(
        DatabricksDirectoryEntry file,
        SqlBulkCopy bulkCopy,
        List<TRecord> buffer,
        TableMigrationResult result,
        CancellationToken cancellationToken)
    {
        await using var parquetStream = await filesClient
            .DownloadAsync(file.Path, cancellationToken)
            .ConfigureAwait(false);

        var stats = new MappingStats();

        await foreach (var batch in ParquetBatchReader
                           .ReadAsync(parquetStream, SourceColumns, cancellationToken)
                           .WithCancellation(cancellationToken)
                           .ConfigureAwait(false))
        {
            if (!_schemaInspected)
            {
                _schemaInspected = true;
                InspectSchema(batch);
            }

            for (var row = 0; row < batch.RowCount; row++)
            {
                result.RowsRead++;

                if (!TryMapRow(batch, row, stats, out var record))
                {
                    continue;
                }

                buffer.Add(record);

                if (buffer.Count >= options.BatchSize)
                {
                    await FlushAsync(bulkCopy, buffer, result, cancellationToken).ConfigureAwait(false);
                }
            }
        }

        result.Absorb(stats);
    }

    private async Task FlushAsync(
        SqlBulkCopy bulkCopy,
        List<TRecord> buffer,
        TableMigrationResult result,
        CancellationToken cancellationToken)
    {
        if (buffer.Count == 0)
        {
            return;
        }

        using var reader = new ObjectDataReader<TRecord>(buffer, Columns);
        await bulkCopy.WriteToServerAsync(reader, cancellationToken).ConfigureAwait(false);

        result.RowsStaged += buffer.Count;
        buffer.Clear();
    }

    private async Task MergeAsync(
        SqlConnection connection,
        TableMigrationResult result,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(connection, MergeSql);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException(
                $"The merge for dbo.{TableName} did not return its Inserted/Updated/Rejected counts.");
        }

        result.RowsInserted = reader.GetInt64(reader.GetOrdinal("Inserted"));
        result.RowsUpdated = reader.GetInt64(reader.GetOrdinal("Updated"));
        result.RowsRejected = reader.GetInt64(reader.GetOrdinal("Rejected"));
    }

    private async Task PruneAsync(
        SqlConnection connection,
        TableMigrationResult result,
        CancellationToken cancellationToken)
    {
        if (!options.PruneMissingRows || PruneSql is not { } pruneSql)
        {
            return;
        }

        // Pruning against nothing would empty the table. An extract that legitimately has no rows is a
        // deliberate act, so it has to go through the delete scripts rather than a side effect of a sync.
        if (result.RowsStaged == 0)
        {
            logger.LogWarning(
                "Skipping the prune of dbo.{TableName}: the staged extract held no rows, and pruning against "
                + "an empty extract would delete every row in the table.",
                TableName);
            return;
        }

        await using var command = CreateCommand(connection, pruneSql);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException(
                $"The prune for dbo.{TableName} did not return its Deleted count.");
        }

        result.RowsDeleted = reader.GetInt64(reader.GetOrdinal("Deleted"));

        if (result.RowsDeleted > 0)
        {
            logger.LogWarning(
                "Pruned {RowsDeleted:N0} row(s) from dbo.{TableName} that the staged extract no longer contains.",
                result.RowsDeleted,
                TableName);
        }
    }

    private async Task<List<DatabricksDirectoryEntry>> ListParquetFilesAsync(
        string folderPath,
        CancellationToken cancellationToken)
    {
        var files = new List<DatabricksDirectoryEntry>();
        var pending = new Queue<string>();
        pending.Enqueue(folderPath);

        while (pending.Count > 0)
        {
            var current = pending.Dequeue();

            await foreach (var entry in filesClient.ListDirectoryAsync(current, cancellationToken)
                               .WithCancellation(cancellationToken)
                               .ConfigureAwait(false))
            {
                if (entry.IsDirectory)
                {
                    // Partitioned writes nest the data files one or more levels deep.
                    pending.Enqueue(entry.Path);
                }
                else if (IsDataFile(entry.Name))
                {
                    files.Add(entry);
                }
            }
        }

        files.Sort((left, right) => string.CompareOrdinal(left.Path, right.Path));
        return files;
    }

    /// <summary>Spark writes bookkeeping entries such as _SUCCESS and .crc alongside the data files.</summary>
    internal static bool IsDataFile(string name) =>
        name.EndsWith(".parquet", StringComparison.OrdinalIgnoreCase)
        && !name.StartsWith('_')
        && !name.StartsWith('.');

    private SqlBulkCopy CreateBulkCopy(SqlConnection connection)
    {
        var bulkCopy = new SqlBulkCopy(connection)
        {
            DestinationTableName = StagingTableName,
            BatchSize = options.BatchSize,
            BulkCopyTimeout = (int)options.SqlCommandTimeout.TotalSeconds
        };

        foreach (var column in Columns)
        {
            bulkCopy.ColumnMappings.Add(column.Name, column.Name);
        }

        return bulkCopy;
    }

    private async Task ExecuteNonQueryAsync(
        SqlConnection connection,
        string commandText,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(connection, commandText);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private SqlCommand CreateCommand(SqlConnection connection, string commandText) =>
        new(commandText, connection) { CommandTimeout = (int)options.SqlCommandTimeout.TotalSeconds };
}

public sealed class EmptySourceFolderException(string message) : Exception(message);
