using MixMix.MigrationWorker.Databricks;
using MixMix.MigrationWorker.Models;
using MixMix.MigrationWorker.Sync;

namespace MixMix.MigrationWorker.Tests;

public class DatabricksPathTests
{
    [Fact]
    public void Volume_paths_keep_their_separators()
    {
        var encoded = DatabricksFilesClient.EncodeVolumePath(
            "/Volumes/demo_catalog/demo_schema/demo_volume/run_20260101_001/item");

        Assert.Equal("/Volumes/demo_catalog/demo_schema/demo_volume/run_20260101_001/item", encoded);
    }

    [Fact]
    public void Segments_that_need_escaping_are_escaped_without_touching_the_separators()
    {
        var encoded = DatabricksFilesClient.EncodeVolumePath("/Volumes/cat/my schema/run #1/part 0.parquet");

        Assert.Equal("/Volumes/cat/my%20schema/run%20%231/part%200.parquet", encoded);
    }

    [Fact]
    public void Repeated_and_trailing_separators_are_collapsed()
    {
        Assert.Equal("/Volumes/cat/item", DatabricksFilesClient.EncodeVolumePath("/Volumes//cat/item/"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void An_empty_path_is_rejected(string path)
    {
        Assert.Throws<ArgumentException>(() => DatabricksFilesClient.EncodeVolumePath(path));
    }
}

public class DataFileFilterTests
{
    [Theory]
    [InlineData("part-00000-abc.snappy.parquet", true)]
    [InlineData("part-00000-abc.parquet", true)]
    [InlineData("_SUCCESS", false)]
    [InlineData("_committed_1234", false)]
    [InlineData("_started_1234", false)]
    [InlineData(".part-00000-abc.parquet.crc", false)]
    [InlineData("notes.txt", false)]
    public void Only_spark_data_files_are_loaded(string name, bool expected)
    {
        // TableMigration<T> is generic, so any closed form exposes the shared static helper.
        Assert.Equal(expected, TableMigration<ItemRecord>.IsDataFile(name));
    }
}
