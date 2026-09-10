namespace MixMix.MigrationWorker.Databricks;

/// <summary>
/// A seekable read stream backed by a temp file that is deleted when the stream is disposed.
/// Parquet requires random access, but the Files API only hands back a forward-only HTTP stream.
/// </summary>
public sealed class TempFileStream : Stream
{
    private readonly FileStream _inner;
    private readonly string _path;
    private bool _disposed;

    private TempFileStream(FileStream inner, string path)
    {
        _inner = inner;
        _path = path;
    }

    public static async Task<TempFileStream> CreateFromAsync(Stream source, CancellationToken cancellationToken)
    {
        var path = Path.Combine(Path.GetTempPath(), $"mixmix-migration-{Guid.NewGuid():N}.parquet");

        var writeStream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 128 * 1024,
            FileOptions.SequentialScan);

        try
        {
            await using (writeStream.ConfigureAwait(false))
            {
                await source.CopyToAsync(writeStream, cancellationToken).ConfigureAwait(false);
            }
        }
        catch
        {
            TryDelete(path);
            throw;
        }

        try
        {
            var readStream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 128 * 1024,
                FileOptions.RandomAccess);

            return new TempFileStream(readStream, path);
        }
        catch
        {
            TryDelete(path);
            throw;
        }
    }

    public override bool CanRead => _inner.CanRead;

    public override bool CanSeek => _inner.CanSeek;

    public override bool CanWrite => false;

    public override long Length => _inner.Length;

    public override long Position
    {
        get => _inner.Position;
        set => _inner.Position = value;
    }

    public override void Flush() => _inner.Flush();

    public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);

    public override int Read(Span<byte> buffer) => _inner.Read(buffer);

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        _inner.ReadAsync(buffer, cancellationToken);

    public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (disposing)
        {
            _inner.Dispose();
            TryDelete(_path);
        }

        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await _inner.DisposeAsync().ConfigureAwait(false);
        TryDelete(_path);
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // A leftover temp file is not worth failing the run over; the OS will reclaim it.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
