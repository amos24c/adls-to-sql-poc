using System.Runtime.CompilerServices;
using Parquet;

namespace MixMix.MigrationWorker.Parquet;

public static class ParquetBatchReader
{
    /// <summary>
    /// Streams a Parquet file one row group at a time, reading only <paramref name="requestedColumns"/>.
    /// Restricting the column set matters here: the staged ItemNew extract carries varbinary(max) image
    /// columns that the target does not use, and Parquet's columnar layout lets us never touch them.
    /// </summary>
    public static async IAsyncEnumerable<ParquetBatch> ReadAsync(
        Stream stream,
        IReadOnlyCollection<string> requestedColumns,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (requestedColumns.Count == 0)
        {
            throw new ArgumentException("At least one column must be requested.", nameof(requestedColumns));
        }

        var wanted = new HashSet<string>(requestedColumns, StringComparer.OrdinalIgnoreCase);

        using var reader = await ParquetReader
            .CreateAsync(stream, leaveStreamOpen: true, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        var fields = reader.Schema.GetDataFields().Where(field => wanted.Contains(field.Name)).ToArray();

        for (var rowGroup = 0; rowGroup < reader.RowGroupCount; rowGroup++)
        {
            using var rowGroupReader = reader.OpenRowGroupReader(rowGroup);
            var columns = new Dictionary<string, Array>(StringComparer.OrdinalIgnoreCase);

            foreach (var field in fields)
            {
                var column = await rowGroupReader.ReadColumnAsync(field, cancellationToken).ConfigureAwait(false);
                columns[field.Name] = column.Data;
            }

            yield return new ParquetBatch(columns, (int)rowGroupReader.RowCount);
        }
    }
}
