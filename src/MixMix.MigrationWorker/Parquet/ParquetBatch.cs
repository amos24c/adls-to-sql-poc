using System.Globalization;

namespace MixMix.MigrationWorker.Parquet;

/// <summary>
/// One Parquet row group, exposed as name-addressable columns. Lookups are case-insensitive and accept
/// several candidate names, because the staged column casing follows whatever the export job produced.
/// </summary>
public sealed class ParquetBatch(IReadOnlyDictionary<string, Array> columns, int rowCount)
{
    public int RowCount { get; } = rowCount;

    public IEnumerable<string> ColumnNames => columns.Keys;

    public bool HasColumn(params string[] candidateNames) => TryGetColumn(candidateNames, out _);

    public int GetInt32(int row, params string[] candidateNames)
    {
        RequireColumn(candidateNames);

        return GetInt32OrNull(row, candidateNames)
               ?? throw new ParquetMappingException(
                   $"Column '{candidateNames[0]}' is null on row {row} but the target column is NOT NULL.");
    }

    public int? GetInt32OrNull(int row, params string[] candidateNames)
    {
        var value = GetValue(row, candidateNames);
        return value is null ? null : Convert.ToInt32(value, CultureInfo.InvariantCulture);
    }

    public string? GetStringOrNull(int row, params string[] candidateNames)
    {
        var value = GetValue(row, candidateNames);
        return value switch
        {
            null => null,
            string text => text,
            _ => Convert.ToString(value, CultureInfo.InvariantCulture)
        };
    }

    public string GetString(int row, params string[] candidateNames)
    {
        RequireColumn(candidateNames);

        return GetStringOrNull(row, candidateNames)
               ?? throw new ParquetMappingException(
                   $"Column '{candidateNames[0]}' is null on row {row} but the target column is NOT NULL.");
    }

    /// <summary>
    /// A required column missing from the extract means the export job and this mapping have drifted apart,
    /// which is worth failing on with the actual schema in the message rather than reporting a null value.
    /// </summary>
    private void RequireColumn(string[] candidateNames)
    {
        if (TryGetColumn(candidateNames, out _))
        {
            return;
        }

        throw new ParquetMappingException(
            $"None of the columns [{string.Join(", ", candidateNames)}] exist in the staged file. "
            + $"Columns read from this file: [{string.Join(", ", ColumnNames)}].");
    }

    public bool? GetBooleanOrNull(int row, params string[] candidateNames)
    {
        var value = GetValue(row, candidateNames);
        return value switch
        {
            null => null,
            bool flag => flag,
            _ => Convert.ToBoolean(value, CultureInfo.InvariantCulture)
        };
    }

    private object? GetValue(int row, string[] candidateNames)
    {
        if (row < 0 || row >= RowCount)
        {
            throw new ArgumentOutOfRangeException(nameof(row), row, $"Row group holds {RowCount} rows.");
        }

        if (!TryGetColumn(candidateNames, out var column))
        {
            return null;
        }

        // A repeated or misaligned column would desynchronise every mapping, so fail loudly instead.
        if (row >= column.Length)
        {
            throw new ParquetMappingException(
                $"Column '{candidateNames[0]}' has {column.Length} values but the row group declares {RowCount} rows.");
        }

        return column.GetValue(row);
    }

    private bool TryGetColumn(string[] candidateNames, out Array column)
    {
        foreach (var name in candidateNames)
        {
            if (columns.TryGetValue(name, out var found))
            {
                column = found;
                return true;
            }
        }

        column = Array.Empty<object>();
        return false;
    }
}

public sealed class ParquetMappingException(string message) : Exception(message);
