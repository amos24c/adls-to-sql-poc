using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MixMix.MigrationWorker.Configuration;
using MixMix.MigrationWorker.Databricks;
using MixMix.MigrationWorker.Sql;

namespace MixMix.MigrationWorker.Sync;

public sealed class MigrationRunner(
    IEnumerable<ITableMigration> migrations,
    IDatabricksFilesClient filesClient,
    MigrationRunStore runStore,
    IOptions<MigrationOptions> options,
    ILogger<MigrationRunner> logger)
{
    private readonly MigrationOptions _options = options.Value;

    // Registration order is the load order and must follow the FK chain.
    private readonly List<ITableMigration> _migrations = migrations.ToList();

    public async Task<MigrationRunSummary> RunAsync(CancellationToken cancellationToken)
    {
        _options.Validate();

        var (runId, runPath) = await ResolveRunAsync(cancellationToken).ConfigureAwait(false);
        var context = new MigrationRunContext(runId, runPath);
        var summary = new MigrationRunSummary(runId, runPath);

        if (_options.EnsureMigrationObjects)
        {
            await runStore.EnsureCreatedAsync(cancellationToken).ConfigureAwait(false);
        }

        logger.LogInformation(
            "Starting sync of run {RunId} from {RunPath} into the target database.",
            runId,
            runPath);

        foreach (var migration in _migrations)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (_options.SkipTables.Contains(migration.TableName))
            {
                logger.LogInformation(
                    "dbo.{TableName} is listed in Migration:SkipTables and is loaded outside this worker; skipping.",
                    migration.TableName);

                summary.Tables.Add(new TableMigrationResult(migration.TableName) { WasSkipped = true });
                continue;
            }

            if (_options.SkipCompletedTables
                && await runStore.HasSucceededAsync(runId, migration.TableName, cancellationToken)
                    .ConfigureAwait(false))
            {
                logger.LogInformation(
                    "dbo.{TableName} already completed for run {RunId}; skipping.",
                    migration.TableName,
                    runId);

                summary.Tables.Add(new TableMigrationResult(migration.TableName) { WasSkipped = true });
                continue;
            }

            await runStore.MarkRunningAsync(runId, migration.TableName, cancellationToken).ConfigureAwait(false);

            try
            {
                var result = await migration.ExecuteAsync(context, cancellationToken).ConfigureAwait(false);
                await runStore.MarkSucceededAsync(runId, result, cancellationToken).ConfigureAwait(false);

                summary.Tables.Add(result);
                LogTableResult(result);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                await runStore
                    .MarkFailedAsync(runId, migration.TableName, exception.Message, cancellationToken)
                    .ConfigureAwait(false);

                summary.Failure = exception;
                summary.FailedTable = migration.TableName;

                logger.LogError(
                    exception,
                    "Loading dbo.{TableName} failed; stopping this pass because the remaining tables depend on it.",
                    migration.TableName);

                return summary;
            }
        }

        logger.LogInformation("Finished sync of run {RunId}.", runId);
        return summary;
    }

    private void LogTableResult(TableMigrationResult result)
    {
        logger.LogInformation(
            "dbo.{TableName}: read {RowsRead:N0}, staged {RowsStaged:N0}, inserted {RowsInserted:N0}, "
            + "updated {RowsUpdated:N0}, filtered {RowsFiltered:N0}, rejected {RowsRejected:N0}, "
            + "deleted {RowsDeleted:N0} from {FilesProcessed} file(s).",
            result.TableName,
            result.RowsRead,
            result.RowsStaged,
            result.RowsInserted,
            result.RowsUpdated,
            result.RowsFiltered,
            result.RowsRejected,
            result.RowsDeleted,
            result.FilesProcessed);

        if (result.ValuesTruncated > 0)
        {
            logger.LogWarning(
                "dbo.{TableName}: {ValuesTruncated:N0} value(s) were shortened to fit a narrower target column.",
                result.TableName,
                result.ValuesTruncated);
        }

        if (result.RowsRejected > 0)
        {
            logger.LogWarning(
                "dbo.{TableName}: {RowsRejected:N0} staged row(s) were discarded as duplicates or as orphans "
                + "with no matching parent row.",
                result.TableName,
                result.RowsRejected);
        }
    }

    private async Task<(string RunId, string RunPath)> ResolveRunAsync(CancellationToken cancellationToken)
    {
        var stageRoot = _options.StageRootPath.TrimEnd('/');

        if (!string.IsNullOrWhiteSpace(_options.RunId))
        {
            return (_options.RunId, $"{stageRoot}/{_options.RunId}");
        }

        // Run folders are date-stamped, so among the prefixed candidates the highest ordinal name is the newest.
        string? newest = null;

        await foreach (var entry in filesClient.ListDirectoryAsync(stageRoot, cancellationToken)
                           .WithCancellation(cancellationToken)
                           .ConfigureAwait(false))
        {
            if (!entry.IsDirectory
                || !entry.Name.StartsWith(_options.RunFolderPrefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (newest is null || string.CompareOrdinal(entry.Name, newest) > 0)
            {
                newest = entry.Name;
            }
        }

        if (newest is null)
        {
            throw new MigrationConfigurationException(
                $"No folders starting with '{_options.RunFolderPrefix}' were found under '{stageRoot}'. "
                + "Set Migration:RunId explicitly, adjust Migration:RunFolderPrefix, or stage a run first.");
        }

        logger.LogInformation("Auto-selected the newest staged run folder {RunId} under {StageRoot}.", newest, stageRoot);
        return (newest, $"{stageRoot}/{newest}");
    }
}
