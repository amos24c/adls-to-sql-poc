namespace MixMix.MigrationWorker.Databricks;

public interface IDatabricksFilesClient
{
    /// <summary>Lists the direct children of a Unity Catalog volume directory, following pagination.</summary>
    IAsyncEnumerable<DatabricksDirectoryEntry> ListDirectoryAsync(
        string directoryPath,
        CancellationToken cancellationToken);

    /// <summary>Downloads a file into a seekable temp-file stream that the caller owns and must dispose.</summary>
    Task<TempFileStream> DownloadAsync(string filePath, CancellationToken cancellationToken);
}
