namespace MixMix.MigrationWorker.Sync;

internal static class TextValue
{
    /// <summary>
    /// Shortens a value to the target column width, counting the truncation so the run report can surface it.
    /// Several target columns are narrower than their source (dbo.Item.Name is nvarchar(100) against a
    /// varchar(200) source), so silently overflowing would fail the whole bulk copy.
    /// </summary>
    public static string? Fit(string? value, int maxLength, MappingStats stats)
    {
        if (value is null || value.Length <= maxLength)
        {
            return value;
        }

        stats.Truncated++;
        return value[..maxLength];
    }
}
