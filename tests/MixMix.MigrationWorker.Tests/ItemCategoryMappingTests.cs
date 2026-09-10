using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MixMix.MigrationWorker.Configuration;
using MixMix.MigrationWorker.Parquet;
using MixMix.MigrationWorker.Sync;

namespace MixMix.MigrationWorker.Tests;

public class ItemCategoryMappingTests
{
    private static ItemCategoryMigration CreateMigration() =>
        new(new UnusedFilesClient(),
            new UnusedConnectionFactory(),
            Options.Create(new MigrationOptions()),
            NullLogger<ItemCategoryMigration>.Instance);

    private static ParquetBatch Batch(params (string Name, Array Data)[] columns) =>
        new(columns.ToDictionary(c => c.Name, c => c.Data, StringComparer.OrdinalIgnoreCase),
            columns[0].Data.Length);

    [Fact]
    public void The_source_Name_column_lands_in_the_target_Descr_column()
    {
        var batch = Batch(("Id", new int[] { 4 }), ("Name", new[] { "Sauces" }));

        Assert.True(CreateMigration().TryMapRow(batch, 0, new MappingStats(), out var record));

        Assert.Equal(4, record.Id);
        Assert.Equal("Sauces", record.Descr);
    }

    [Fact]
    public void A_staged_extract_that_already_uses_Descr_is_accepted()
    {
        var batch = Batch(("Id", new int[] { 4 }), ("Descr", new[] { "Sauces" }));

        Assert.True(CreateMigration().TryMapRow(batch, 0, new MappingStats(), out var record));
        Assert.Equal("Sauces", record.Descr);
    }

    [Fact]
    public void Descr_is_shortened_to_the_hundred_character_target_column()
    {
        var batch = Batch(("Id", new int[] { 4 }), ("Name", new[] { new string('c', 140) }));
        var stats = new MappingStats();

        Assert.True(CreateMigration().TryMapRow(batch, 0, stats, out var record));

        Assert.Equal(100, record.Descr!.Length);
        Assert.Equal(1, stats.Truncated);
    }

    [Fact]
    public void Categories_are_loaded_regardless_of_their_deleted_flag()
    {
        // Dropping a category would orphan every item pointing at it.
        var batch = Batch(
            ("Id", new int[] { 4 }),
            ("Name", new[] { "Retired" }),
            ("Deleted", new bool?[] { true }),
            ("Active", new bool?[] { false }));

        var stats = new MappingStats();

        Assert.True(CreateMigration().TryMapRow(batch, 0, stats, out _));
        Assert.Equal(0, stats.Filtered);
    }
}
