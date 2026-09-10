using System.Text.Json.Serialization;

namespace MixMix.MigrationWorker.Databricks;

public sealed class DatabricksDirectoryEntry
{
    [JsonPropertyName("path")]
    public string Path { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("is_directory")]
    public bool IsDirectory { get; set; }

    [JsonPropertyName("file_size")]
    public long FileSize { get; set; }

    [JsonPropertyName("last_modified")]
    public long LastModified { get; set; }
}

internal sealed class ListDirectoryResponse
{
    [JsonPropertyName("contents")]
    public List<DatabricksDirectoryEntry>? Contents { get; set; }

    [JsonPropertyName("next_page_token")]
    public string? NextPageToken { get; set; }
}
