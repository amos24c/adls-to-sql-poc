using MixMix.MigrationWorker.Parquet;
using Parquet;
using Parquet.Data;
using Parquet.Schema;

namespace MixMix.MigrationWorker.Tests;

/// <summary>Round-trips real Parquet files so the reader is verified against the library, not against a mock.</summary>
public class ParquetBatchReaderTests
{
    private static async Task<MemoryStream> WriteItemFileAsync(int rowGroups = 1)
    {
        var itemId = new DataField<int>("ItemID");
        var name = new DataField<string>("Name");
        var deleted = new DataField<bool?>("Deleted");
        var image = new DataField<byte[]>("Image");
        var schema = new ParquetSchema(itemId, name, deleted, image);

        var buffer = new MemoryStream();

        await using (var writer = await ParquetWriter.CreateAsync(schema, buffer))
        {
            for (var group = 0; group < rowGroups; group++)
            {
                using var rowGroup = writer.CreateRowGroup();
                var offset = group * 2;

                await rowGroup.WriteColumnAsync(new DataColumn(itemId, new[] { offset + 1, offset + 2 }));
                await rowGroup.WriteColumnAsync(new DataColumn(name, new[] { $"item-{offset + 1}", null }));
                await rowGroup.WriteColumnAsync(new DataColumn(deleted, new bool?[] { false, true }));
                await rowGroup.WriteColumnAsync(
                    new DataColumn(image, new[] { new byte[] { 1, 2, 3 }, new byte[] { 4 } }));
            }
        }

        // Disposing the writer flushes the footer and closes the stream, so hand back a fresh readable copy.
        return new MemoryStream(buffer.ToArray());
    }

    [Fact]
    public async Task Requested_columns_round_trip_with_their_values_and_nulls()
    {
        await using var stream = await WriteItemFileAsync();

        var batches = new List<ParquetBatch>();
        await foreach (var batch in ParquetBatchReader.ReadAsync(stream, ["ItemID", "Name", "Deleted"], default))
        {
            batches.Add(batch);
        }

        var only = Assert.Single(batches);
        Assert.Equal(2, only.RowCount);
        Assert.Equal(1, only.GetInt32(0, "ItemID"));
        Assert.Equal("item-1", only.GetStringOrNull(0, "Name"));
        Assert.Null(only.GetStringOrNull(1, "Name"));
        Assert.False(only.GetBooleanOrNull(0, "Deleted"));
        Assert.True(only.GetBooleanOrNull(1, "Deleted"));
    }

    [Fact]
    public async Task Columns_that_were_not_requested_are_never_decoded()
    {
        await using var stream = await WriteItemFileAsync();

        await foreach (var batch in ParquetBatchReader.ReadAsync(stream, ["ItemID"], default))
        {
            // Skipping the varbinary(max) image columns is the whole point of the narrow column list.
            Assert.Equal(["ItemID"], batch.ColumnNames);
            Assert.False(batch.HasColumn("Image"));
        }
    }

    [Fact]
    public async Task Each_row_group_is_yielded_as_its_own_batch()
    {
        await using var stream = await WriteItemFileAsync(rowGroups: 3);

        var ids = new List<int>();
        await foreach (var batch in ParquetBatchReader.ReadAsync(stream, ["ItemID"], default))
        {
            for (var row = 0; row < batch.RowCount; row++)
            {
                ids.Add(batch.GetInt32(row, "ItemID"));
            }
        }

        Assert.Equal([1, 2, 3, 4, 5, 6], ids);
    }

    [Fact]
    public async Task Requesting_no_columns_is_rejected()
    {
        await using var stream = await WriteItemFileAsync();

        await Assert.ThrowsAsync<ArgumentException>(async () =>
        {
            await foreach (var _ in ParquetBatchReader.ReadAsync(stream, [], default))
            {
            }
        });
    }
}
