using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using MixMix.MigrationWorker.Databricks;
using MixMix.MigrationWorker.Sql;

namespace MixMix.MigrationWorker.Tests;

/// <summary>Stands in for the real dependencies when a test only exercises row mapping.</summary>
internal sealed class UnusedFilesClient : IDatabricksFilesClient
{
    public IAsyncEnumerable<DatabricksDirectoryEntry> ListDirectoryAsync(
        string directoryPath,
        CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task<TempFileStream> DownloadAsync(string filePath, CancellationToken cancellationToken) =>
        throw new NotSupportedException();
}

internal sealed class UnusedConnectionFactory : ISqlConnectionFactory
{
    public Task<SqlConnection> OpenAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
}

internal sealed class CapturingLogger<T> : ILogger<T>
{
    public List<(LogLevel Level, string Message)> Entries { get; } = [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter) =>
        Entries.Add((logLevel, formatter(state, exception)));

    public IEnumerable<string> Warnings =>
        Entries.Where(e => e.Level == LogLevel.Warning).Select(e => e.Message);
}
