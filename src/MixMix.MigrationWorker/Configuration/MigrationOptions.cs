namespace MixMix.MigrationWorker.Configuration;

public sealed class MigrationOptions
{
    public const string SectionName = "Migration";

    /// <summary>
    /// Unity Catalog volume folder holding the run folders, in the form
    /// /Volumes/&lt;catalog&gt;/&lt;schema&gt;/&lt;volume&gt;. Leave <see cref="RunId"/> empty to auto-discover
    /// the newest run folder underneath it.
    /// </summary>
    public string StageRootPath { get; set; } = "/Volumes/<catalog>/<schema>/<volume>";

    /// <summary>Run folder name under <see cref="StageRootPath"/>. When empty the newest folder is used.</summary>
    public string? RunId { get; set; }

    /// <summary>
    /// Only folders starting with this prefix are candidates when <see cref="RunId"/> is empty. The staging
    /// volume also holds scratch folders such as 'stage' and 'test', which sort after 'run_...' and would
    /// otherwise be picked as the newest run.
    /// </summary>
    public string RunFolderPrefix { get; set; } = "run_";

    /// <summary>Folder name per table, relative to the resolved run folder.</summary>
    public Dictionary<string, string> SourceFolders { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ItemCategory"] = "itemcategory",
        ["Blob"] = "blob",
        ["Item"] = "item",
        ["ItemBlob"] = "itemblob"
    };

    /// <summary>Rows buffered in memory before each SqlBulkCopy flush into the staging table.</summary>
    public int BatchSize { get; set; } = 5_000;

    public TimeSpan SqlCommandTimeout { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>Interval between sync passes. Ignored when <see cref="RunOnce"/> is true.</summary>
    public TimeSpan Interval { get; set; } = TimeSpan.FromHours(1);

    /// <summary>When true the worker performs a single pass and then stops the host.</summary>
    public bool RunOnce { get; set; }

    /// <summary>Skip tables already recorded as succeeded for the resolved run id, making reruns cheap.</summary>
    public bool SkipCompletedTables { get; set; } = true;

    /// <summary>
    /// Delete target rows that the staged extract no longer contains, so rows deleted at the source (or
    /// filtered out by <see cref="ExcludeDeletedItems"/>) eventually disappear from the target.
    /// <para>
    /// Off by default, and deliberately so: with this on, an extract that ran against a partly-populated
    /// source deletes good target rows. Only turn it on when the extract is known to be a complete snapshot.
    /// Implemented for dbo.Item and dbo.ItemBlob; pruning dbo.Blob or dbo.ItemCategory would cascade into
    /// their children and is not supported.
    /// </para>
    /// </summary>
    public bool PruneMissingRows { get; set; }

    /// <summary>
    /// Tables the worker must not touch because they are loaded some other way. Used for tables that are not
    /// staged in the volume; see sql/03_seed_itemcategory.sql. A skipped table is still expected to be
    /// populated, since the tables that follow it depend on its rows existing.
    /// </summary>
    public HashSet<string> SkipTables { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Source rows flagged as deleted are not carried into the target.</summary>
    public bool ExcludeDeletedItems { get; set; } = true;

    /// <summary>Source rows flagged as inactive are not carried into the target.</summary>
    public bool ExcludeInactiveItems { get; set; }

    /// <summary>
    /// Reseed the identity columns on dbo.Item and dbo.ItemCategory after loading explicit ids, so later
    /// application inserts do not collide with the migrated rows.
    /// </summary>
    public bool ReseedIdentityColumns { get; set; } = true;

    /// <summary>Create the migration bookkeeping table on startup if it is missing.</summary>
    public bool EnsureMigrationObjects { get; set; } = true;

    /// <summary>
    /// When false, a staged folder with no Parquet files fails the table instead of being recorded as a
    /// successful no-op. That matters because a success is remembered and skipped on the next pass, which
    /// would quietly bury an export that had not finished writing. Set true only if a table is legitimately empty.
    /// </summary>
    public bool AllowEmptySourceFolders { get; set; }

    public string GetSourceFolder(string tableName) =>
        SourceFolders.TryGetValue(tableName, out var folder) && !string.IsNullOrWhiteSpace(folder)
            ? folder
            : throw new MigrationConfigurationException(
                $"{SectionName}:{nameof(SourceFolders)} has no folder configured for table '{tableName}'.");

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(StageRootPath))
        {
            throw new MigrationConfigurationException($"{SectionName}:{nameof(StageRootPath)} must be set.");
        }

        if (!StageRootPath.StartsWith('/'))
        {
            throw new MigrationConfigurationException(
                $"{SectionName}:{nameof(StageRootPath)} must be an absolute volume path starting with '/', "
                + $"but was '{StageRootPath}'.");
        }

        // Caught here rather than left to the Files API, which rejects a template path with a bare
        // 'Invalid path' that does not say the setting was never filled in.
        if (StageRootPath.Contains('<') || StageRootPath.Contains('>'))
        {
            throw new MigrationConfigurationException(
                $"{SectionName}:{nameof(StageRootPath)} is still a placeholder ('{StageRootPath}'). Set it to "
                + "your own volume, in the form /Volumes/<catalog>/<schema>/<volume>.");
        }

        if (BatchSize <= 0)
        {
            throw new MigrationConfigurationException($"{SectionName}:{nameof(BatchSize)} must be greater than zero.");
        }

        if (!RunOnce && Interval <= TimeSpan.Zero)
        {
            throw new MigrationConfigurationException($"{SectionName}:{nameof(Interval)} must be greater than zero.");
        }
    }
}
