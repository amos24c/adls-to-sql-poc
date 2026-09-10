using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MixMix.MigrationWorker.Configuration;
using MixMix.MigrationWorker.Parquet;
using MixMix.MigrationWorker.Sync;

namespace MixMix.MigrationWorker.Tests;

public class BlobMappingTests
{
    private static BlobMigration CreateMigration() =>
        new(new UnusedFilesClient(),
            new UnusedConnectionFactory(),
            Options.Create(new MigrationOptions()),
            NullLogger<BlobMigration>.Instance);

    private static ParquetBatch Batch(params (string Name, Array Data)[] columns) =>
        new(columns.ToDictionary(c => c.Name, c => c.Data, StringComparer.OrdinalIgnoreCase),
            columns[0].Data.Length);

    [Fact]
    public void A_blob_row_maps_straight_across()
    {
        var batch = Batch(("BlobId", new int[] { 9 }), ("Name", new[] { "hero.jpg" }));

        Assert.True(CreateMigration().TryMapRow(batch, 0, new MappingStats(), out var record));

        Assert.Equal(9, record.BlobId);
        Assert.Equal("hero.jpg", record.Name);
    }

    [Fact]
    public void A_null_name_is_dropped_rather_than_failing_the_bulk_copy()
    {
        // dbo.Blob.Name is NOT NULL, so a null would abort the whole batch instead of one row.
        var batch = Batch(("BlobId", new int[] { 9 }), ("Name", new string?[] { null }));
        var stats = new MappingStats();

        Assert.False(CreateMigration().TryMapRow(batch, 0, stats, out _));
        Assert.Equal(1, stats.Filtered);
    }

    [Fact]
    public void A_name_wider_than_the_target_column_is_shortened()
    {
        var batch = Batch(("BlobId", new int[] { 9 }), ("Name", new[] { new string('n', 80) }));
        var stats = new MappingStats();

        Assert.True(CreateMigration().TryMapRow(batch, 0, stats, out var record));

        Assert.Equal(50, record.Name.Length);
        Assert.Equal(1, stats.Truncated);
    }
}
