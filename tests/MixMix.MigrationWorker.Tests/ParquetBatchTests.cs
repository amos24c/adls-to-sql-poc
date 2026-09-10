using MixMix.MigrationWorker.Parquet;

namespace MixMix.MigrationWorker.Tests;

public class ParquetBatchTests
{
    private static ParquetBatch Batch(params (string Name, Array Data)[] columns) =>
        new(columns.ToDictionary(c => c.Name, c => c.Data, StringComparer.OrdinalIgnoreCase),
            columns[0].Data.Length);

    [Fact]
    public void GetInt32_widens_from_the_int64_that_parquet_often_carries()
    {
        var batch = Batch(("ItemID", new long[] { 42L }));

        Assert.Equal(42, batch.GetInt32(0, "ItemID"));
    }

    [Fact]
    public void GetInt32_reads_a_nullable_column()
    {
        var batch = Batch(("ItemID", new int?[] { 7 }));

        Assert.Equal(7, batch.GetInt32(0, "ItemID"));
    }

    [Fact]
    public void Column_lookup_ignores_case_and_falls_through_the_candidate_names()
    {
        var batch = Batch(("itemid", new int[] { 5 }));

        Assert.Equal(5, batch.GetInt32(0, "ItemID", "ItemId", "Id"));
    }

    [Fact]
    public void Candidate_names_are_tried_in_order()
    {
        var batch = Batch(("Id", new int[] { 1 }), ("ItemID", new int[] { 2 }));

        Assert.Equal(2, batch.GetInt32(0, "ItemID", "Id"));
    }

    [Fact]
    public void A_required_column_that_is_absent_reports_the_columns_that_were_present()
    {
        var batch = Batch(("Name", new[] { "widget" }));

        var exception = Assert.Throws<ParquetMappingException>(() => batch.GetInt32(0, "ItemID", "Id"));

        Assert.Contains("ItemID, Id", exception.Message);
        Assert.Contains("Name", exception.Message);
    }

    [Fact]
    public void A_required_column_that_is_null_is_reported_as_a_null_not_as_missing()
    {
        var batch = Batch(("ItemID", new int?[] { null }));

        var exception = Assert.Throws<ParquetMappingException>(() => batch.GetInt32(0, "ItemID"));

        Assert.Contains("NOT NULL", exception.Message);
    }

    [Fact]
    public void An_optional_column_that_is_absent_reads_as_null_rather_than_throwing()
    {
        var batch = Batch(("Name", new[] { "widget" }));

        Assert.Null(batch.GetBooleanOrNull(0, "Deleted"));
        Assert.Null(batch.GetInt32OrNull(0, "Sort"));
    }

    [Fact]
    public void GetBooleanOrNull_reads_the_bit_columns_used_for_filtering()
    {
        var batch = Batch(("Deleted", new bool?[] { true, false, null }));

        Assert.True(batch.GetBooleanOrNull(0, "Deleted"));
        Assert.False(batch.GetBooleanOrNull(1, "Deleted"));
        Assert.Null(batch.GetBooleanOrNull(2, "Deleted"));
    }

    [Fact]
    public void Reading_past_the_row_count_is_rejected()
    {
        var batch = Batch(("ItemID", new int[] { 1 }));

        Assert.Throws<ArgumentOutOfRangeException>(() => batch.GetInt32(1, "ItemID"));
    }

    [Fact]
    public void A_column_shorter_than_the_row_count_fails_rather_than_silently_desynchronising()
    {
        // A repeated field would produce more values than rows and misalign every later row.
        var batch = new ParquetBatch(
            new Dictionary<string, Array>(StringComparer.OrdinalIgnoreCase) { ["ItemID"] = new int[] { 1 } },
            rowCount: 3);

        Assert.Throws<ParquetMappingException>(() => batch.GetInt32(2, "ItemID"));
    }
}
