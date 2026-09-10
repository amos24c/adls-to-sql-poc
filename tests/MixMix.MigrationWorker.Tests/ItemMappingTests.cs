using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MixMix.MigrationWorker.Configuration;
using MixMix.MigrationWorker.Models;
using MixMix.MigrationWorker.Parquet;
using MixMix.MigrationWorker.Sync;

namespace MixMix.MigrationWorker.Tests;

public class ItemMappingTests
{
    private static ItemMigration CreateMigration(Action<MigrationOptions>? configure = null)
    {
        var options = new MigrationOptions();
        configure?.Invoke(options);

        return new ItemMigration(
            new UnusedFilesClient(),
            new UnusedConnectionFactory(),
            Options.Create(options),
            NullLogger<ItemMigration>.Instance);
    }

    private static ParquetBatch Batch(params (string Name, Array Data)[] columns) =>
        new(columns.ToDictionary(c => c.Name, c => c.Data, StringComparer.OrdinalIgnoreCase),
            columns[0].Data.Length);

    [Fact]
    public void The_source_ItemID_becomes_the_target_Id_so_ItemBlob_keeps_pointing_at_the_right_row()
    {
        var migration = CreateMigration();
        var batch = Batch(
            ("ItemID", new int[] { 3001 }),
            ("CategoryId", new int[] { 12 }),
            ("Name", new[] { "Adobo Mix" }));

        var mapped = migration.TryMapRow(batch, 0, new MappingStats(), out var record);

        Assert.True(mapped);
        Assert.Equal(new ItemRecord(3001, 12, "Adobo Mix"), record);
    }

    [Fact]
    public void A_name_longer_than_the_target_column_is_shortened_and_counted()
    {
        var migration = CreateMigration();
        var stats = new MappingStats();
        var longName = new string('x', 200);

        var batch = Batch(
            ("ItemID", new int[] { 1 }),
            ("CategoryId", new int[] { 1 }),
            ("Name", new[] { longName }));

        Assert.True(migration.TryMapRow(batch, 0, stats, out var record));

        // dbo.Item.Name is nvarchar(100) against a varchar(200) source.
        Assert.Equal(100, record.Name!.Length);
        Assert.Equal(1, stats.Truncated);
    }

    [Fact]
    public void A_name_that_already_fits_is_left_alone()
    {
        var migration = CreateMigration();
        var stats = new MappingStats();

        var batch = Batch(
            ("ItemID", new int[] { 1 }),
            ("CategoryId", new int[] { 1 }),
            ("Name", new[] { "Sinigang Mix" }));

        Assert.True(migration.TryMapRow(batch, 0, stats, out var record));

        Assert.Equal("Sinigang Mix", record.Name);
        Assert.Equal(0, stats.Truncated);
    }

    [Fact]
    public void A_null_name_stays_null_because_the_target_column_is_nullable()
    {
        var migration = CreateMigration();

        var batch = Batch(
            ("ItemID", new int[] { 1 }),
            ("CategoryId", new int[] { 1 }),
            ("Name", new string?[] { null }));

        Assert.True(migration.TryMapRow(batch, 0, new MappingStats(), out var record));
        Assert.Null(record.Name);
    }

    [Fact]
    public void Deleted_rows_are_dropped_by_default()
    {
        var migration = CreateMigration();
        var stats = new MappingStats();

        var batch = Batch(
            ("ItemID", new int[] { 1 }),
            ("CategoryId", new int[] { 1 }),
            ("Name", new[] { "gone" }),
            ("Deleted", new bool?[] { true }));

        Assert.False(migration.TryMapRow(batch, 0, stats, out _));
        Assert.Equal(1, stats.Filtered);
    }

    [Fact]
    public void Deleted_rows_are_kept_when_the_filter_is_turned_off()
    {
        var migration = CreateMigration(options => options.ExcludeDeletedItems = false);

        var batch = Batch(
            ("ItemID", new int[] { 1 }),
            ("CategoryId", new int[] { 1 }),
            ("Name", new[] { "kept" }),
            ("Deleted", new bool?[] { true }));

        Assert.True(migration.TryMapRow(batch, 0, new MappingStats(), out _));
    }

    [Fact]
    public void A_null_Deleted_flag_is_not_treated_as_deleted()
    {
        var migration = CreateMigration();

        var batch = Batch(
            ("ItemID", new int[] { 1 }),
            ("CategoryId", new int[] { 1 }),
            ("Name", new[] { "kept" }),
            ("Deleted", new bool?[] { null }));

        Assert.True(migration.TryMapRow(batch, 0, new MappingStats(), out _));
    }

    [Fact]
    public void Pruning_removes_child_rows_before_the_items_they_reference()
    {
        // Deleting an item whose dbo.ItemBlob rows still point at it would violate the foreign key.
        var pruneSql = CreateMigration().PruneSql;

        Assert.NotNull(pruneSql);
        Assert.True(
            pruneSql!.IndexOf("dbo.ItemBlob", StringComparison.Ordinal)
            < pruneSql.IndexOf("dbo.Item AS t", StringComparison.Ordinal),
            "The child delete must come first.");

        // Comparing against the staging table, not the merge's eligible set, keeps rows that were merely
        // held back for a missing parent.
        Assert.Contains("#stage_Item", pruneSql);
        Assert.DoesNotContain("#src_Item", pruneSql);
    }

    [Fact]
    public void An_enabled_filter_whose_column_was_never_staged_is_reported()
    {
        // Otherwise ExcludeDeletedItems silently passes every row through and deleted rows reach the target.
        var logger = new CapturingLogger<ItemMigration>();
        var migration = new ItemMigration(
            new UnusedFilesClient(),
            new UnusedConnectionFactory(),
            Options.Create(new MigrationOptions()),
            logger);

        migration.InspectSchema(Batch(
            ("Id", new int[] { 1 }),
            ("CategoryId", new int[] { 1 }),
            ("Name", new[] { "no deleted column here" })));

        var warning = Assert.Single(logger.Warnings);
        Assert.Contains("ExcludeDeletedItems", warning);
        Assert.Contains("Deleted", warning);
    }

    [Fact]
    public void No_warning_is_raised_when_the_filter_column_is_present()
    {
        var logger = new CapturingLogger<ItemMigration>();
        var migration = new ItemMigration(
            new UnusedFilesClient(),
            new UnusedConnectionFactory(),
            Options.Create(new MigrationOptions()),
            logger);

        migration.InspectSchema(Batch(
            ("Id", new int[] { 1 }),
            ("CategoryId", new int[] { 1 }),
            ("Deleted", new bool?[] { false })));

        Assert.Empty(logger.Warnings);
    }

    [Fact]
    public void Inactive_rows_are_dropped_only_when_that_filter_is_turned_on()
    {
        var batch = Batch(
            ("ItemID", new int[] { 1 }),
            ("CategoryId", new int[] { 1 }),
            ("Name", new[] { "inactive" }),
            ("Active", new bool?[] { false }));

        Assert.True(CreateMigration().TryMapRow(batch, 0, new MappingStats(), out _));

        var strict = CreateMigration(options => options.ExcludeInactiveItems = true);
        Assert.False(strict.TryMapRow(batch, 0, new MappingStats(), out _));
    }
}
