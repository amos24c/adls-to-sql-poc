namespace MixMix.MigrationWorker.Sync;

/// <summary>Accumulates what happened to one table during a run, for logging and for the bookkeeping table.</summary>
public sealed class TableMigrationResult(string tableName)
{
    public string TableName { get; } = tableName;

    /// <summary>True when the table was already recorded as succeeded for this run and was left alone.</summary>
    public bool WasSkipped { get; set; }

    public int FilesProcessed { get; set; }

    /// <summary>Rows read out of the staged Parquet files.</summary>
    public long RowsRead { get; set; }

    /// <summary>Rows dropped while mapping, for example source rows flagged as deleted.</summary>
    public long RowsFiltered { get; set; }

    /// <summary>Rows that reached the staging table.</summary>
    public long RowsStaged { get; set; }

    public long RowsInserted { get; set; }

    public long RowsUpdated { get; set; }

    /// <summary>Staged rows the merge discarded as duplicates or as orphans with no parent row.</summary>
    public long RowsRejected { get; set; }

    /// <summary>Target rows deleted because the staged extract no longer contained them. Requires opt-in.</summary>
    public long RowsDeleted { get; set; }

    /// <summary>Values shortened to fit a narrower target column.</summary>
    public long ValuesTruncated { get; set; }

    public void Absorb(MappingStats stats)
    {
        RowsFiltered += stats.Filtered;
        ValuesTruncated += stats.Truncated;
    }
}

/// <summary>Per-row mapping diagnostics, kept separate so mappers do not need the whole result object.</summary>
public sealed class MappingStats
{
    public long Filtered { get; set; }

    public long Truncated { get; set; }
}
