namespace MixMix.MigrationWorker.Sync;

public sealed class MigrationRunSummary(string runId, string runPath)
{
    public string RunId { get; } = runId;

    public string RunPath { get; } = runPath;

    public List<TableMigrationResult> Tables { get; } = [];

    /// <summary>Set when a table failed; the remaining tables are not attempted because they depend on it.</summary>
    public Exception? Failure { get; set; }

    public string? FailedTable { get; set; }

    public bool Succeeded => Failure is null;
}
