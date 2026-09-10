using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;

namespace MixMix.MigrationWorker.Databricks;

/// <summary>
/// Reads staged files straight out of a Unity Catalog volume through the Databricks Files API:
/// GET /api/2.0/fs/directories{path} to list, GET /api/2.0/fs/files{path} to download.
/// </summary>
public sealed class DatabricksFilesClient(HttpClient httpClient, ILogger<DatabricksFilesClient> logger)
    : IDatabricksFilesClient
{
    private const int PageSize = 1000;

    public async IAsyncEnumerable<DatabricksDirectoryEntry> ListDirectoryAsync(
        string directoryPath,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var encodedPath = EncodeVolumePath(directoryPath);
        string? pageToken = null;

        do
        {
            var requestUri = $"api/2.0/fs/directories{encodedPath}?page_size={PageSize}";
            if (pageToken is not null)
            {
                requestUri += $"&page_token={Uri.EscapeDataString(pageToken)}";
            }

            using var response = await SendAsync(requestUri, cancellationToken).ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                throw new DatabricksFilesException(
                    $"Volume directory '{directoryPath}' was not found. Check the staged run path and that the "
                    + "identity in use has READ VOLUME on it.");
            }

            await EnsureSuccessAsync(response, directoryPath, cancellationToken).ConfigureAwait(false);

            var page = await response.Content
                .ReadFromJsonAsync<ListDirectoryResponse>(cancellationToken)
                .ConfigureAwait(false);

            foreach (var entry in page?.Contents ?? [])
            {
                yield return entry;
            }

            pageToken = page?.NextPageToken;
        }
        while (!string.IsNullOrEmpty(pageToken));
    }

    public async Task<TempFileStream> DownloadAsync(string filePath, CancellationToken cancellationToken)
    {
        var requestUri = $"api/2.0/fs/files{EncodeVolumePath(filePath)}";

        using var response = await SendAsync(requestUri, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, filePath, cancellationToken).ConfigureAwait(false);

        await using var content = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var spooled = await TempFileStream.CreateFromAsync(content, cancellationToken).ConfigureAwait(false);

        logger.LogDebug("Downloaded {Path} ({Bytes:N0} bytes) from the Databricks volume.", filePath, spooled.Length);
        return spooled;
    }

    /// <summary>
    /// A wrong workspace host fails as a bare DNS error that names no setting, so the workspace URL is
    /// named explicitly here rather than leaving the caller to infer it from a socket exception.
    /// </summary>
    private async Task<HttpResponseMessage> SendAsync(string requestUri, CancellationToken cancellationToken)
    {
        try
        {
            return await httpClient
                .GetAsync(requestUri, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (HttpRequestException exception)
            when (exception.InnerException is SocketException
            {
                SocketErrorCode: SocketError.HostNotFound or SocketError.NoData or SocketError.TryAgain
            })
        {
            throw new DatabricksFilesException(
                $"The Databricks workspace host '{httpClient.BaseAddress?.Host}' could not be resolved. "
                + "Check Databricks:WorkspaceUrl: it should be your own workspace URL, in the form "
                + "https://adb-<16-digit-workspace-id>.<n>.azuredatabricks.net, which you can copy from the "
                + "browser address bar with the workspace open.",
                exception);
        }
    }

    /// <summary>
    /// Volume paths are absolute and slash-delimited; each segment is escaped while the separators are kept intact.
    /// </summary>
    internal static string EncodeVolumePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("Volume path must not be empty.", nameof(path));
        }

        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return "/" + string.Join('/', segments.Select(Uri.EscapeDataString));
    }

    private static async Task EnsureSuccessAsync(
        HttpResponseMessage response,
        string path,
        CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        var hint = response.StatusCode switch
        {
            HttpStatusCode.Unauthorized =>
                " The credential was rejected outright, which usually means it is not a Databricks token at all "
                + "(a personal access token starts with 'dapi'). Check Databricks:PersonalAccessToken.",
            HttpStatusCode.Forbidden =>
                " The credential was recognised but refused, so it is expired, revoked, or lacks READ VOLUME on "
                + "this volume. Check Databricks:PersonalAccessToken and the grants on the migration volume.",
            _ => string.Empty
        };

        throw new DatabricksFilesException(
            $"Databricks Files API returned {(int)response.StatusCode} {response.ReasonPhrase} for '{path}'. "
            + $"{body}{hint}");
    }
}

public sealed class DatabricksFilesException(string message, Exception? innerException = null)
    : Exception(message, innerException);
