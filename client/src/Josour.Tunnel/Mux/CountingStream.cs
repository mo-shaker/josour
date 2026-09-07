namespace Josour.Tunnel.Mux;

/// <summary>غلاف يعدّ البايتات المقروءة والمكتوبة ويمرر الإغلاق النصفي إلى الداخل. يملك الـ stream الداخلي.</summary>
public sealed class CountingStream : Stream, IHalfClosable
{
    private readonly Stream _inner;
    private readonly Action? _onDispose;
    private long _bytesRead;
    private long _bytesWritten;
    private int _disposed;

    public CountingStream(Stream inner, Action? onDispose = null)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _onDispose = onDispose;
    }

    public long BytesRead => Volatile.Read(ref _bytesRead);
    public long BytesWritten => Volatile.Read(ref _bytesWritten);
    public Stream Inner => _inner;

    public override bool CanRead => _inner.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => _inner.CanWrite;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override int Read(byte[] buffer, int offset, int count)
    {
        var n = _inner.Read(buffer, offset, count);
        Interlocked.Add(ref _bytesRead, n);
        return n;
    }

    public override int Read(Span<byte> buffer)
    {
        var n = _inner.Read(buffer);
        Interlocked.Add(ref _bytesRead, n);
        return n;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var n = await _inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        Interlocked.Add(ref _bytesRead, n);
        return n;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override void Write(byte[] buffer, int offset, int count)
    {
        _inner.Write(buffer, offset, count);
        Interlocked.Add(ref _bytesWritten, count);
    }

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        _inner.Write(buffer);
        Interlocked.Add(ref _bytesWritten, buffer.Length);
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        await _inner.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
        Interlocked.Add(ref _bytesWritten, buffer.Length);
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override void Flush() => _inner.Flush();
    public override Task FlushAsync(CancellationToken cancellationToken) => _inner.FlushAsync(cancellationToken);
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    public ValueTask CompleteWritingAsync(CancellationToken ct)
        => _inner is IHalfClosable h ? h.CompleteWritingAsync(ct) : ValueTask.CompletedTask;

    protected override void Dispose(bool disposing)
    {
        if (disposing && Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _inner.Dispose();
            _onDispose?.Invoke();
        }
        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            await _inner.DisposeAsync().ConfigureAwait(false);
            _onDispose?.Invoke();
        }
        await base.DisposeAsync().ConfigureAwait(false);
    }
}
