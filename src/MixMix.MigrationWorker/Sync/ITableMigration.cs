namespace MixMix.MigrationWorker.Sync;

/// <summary>One staged table to load. Registration order defines load order, which must follow the FK chain.</summary>
public interface ITableMigration
{
    string TableName { get; }

    Task<TableMigrationResult> ExecuteAsync(MigrationRunContext context, CancellationToken cancellationToken);
}

    /// <param name="RunId">Identifier of the staged run, for example run_20260101_001.</param>
/// <param name="RunPath">Absolute volume path of the run folder holding the per-table subfolders.</param>
public sealed record MigrationRunContext(string RunId, string RunPath);
