using System.Buffers;
using System.IO.Pipelines;

namespace Josour.Tunnel.Mux;

/// <summary>
/// A Stream over a PipeReader/PipeWriter pair (a Nerdbank channel). Writing flushes at once (FlushAsync), so it is subject to the remote window's pressure.
/// The half-close = Output.Complete. Disposing completes both directions and waits for the channel to complete with a short timeout, then breaks it forcibly.
/// </summary>
public sealed class PipeDuplexStream : Stream, IHalfClosable
{
    private static readonly TimeSpan GracefulCompletion = TimeSpan.FromSeconds(3);

    private readonly PipeReader _reader;
    private readonly PipeWriter _writer;
    private readonly Task? _completion;
    private readonly Action? _breakGlass;
    private readonly Action? _onDispose;
    private int _writeCompleted;
    private int _readCompleted;
    private int _disposed;

    /// <param name="completion">Completes when the channel is closed from both ends (optional).</param>
    /// <param name="breakGlass">A forced termination of the channel if it does not complete within the timeout (optional).</param>
    public PipeDuplexStream(PipeReader reader, PipeWriter writer, Task? completion = null, Action? breakGlass = null, Action? onDispose = null)
    {
        _reader = reader ?? throw new ArgumentNullException(nameof(reader));
        _writer = writer ?? throw new ArgumentNullException(nameof(writer));
        _completion = completion;
        _breakGlass = breakGlass;
        _onDispose = onDispose;
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (buffer.Length == 0) return 0;
        if (Volatile.Read(ref _readCompleted) != 0) return 0;
        while (true)
        {
            ReadResult result;
            try
            {
                result = await _reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (InvalidOperationException e)
            {
                // The reader completed while reading (a concurrent Dispose)
                if (Volatile.Read(ref _readCompleted) != 0) return 0;
                throw new IOException("mux stream read failed", e);
            }

            var sequence = result.Buffer;
            if (!sequence.IsEmpty)
            {
                var n = (int)Math.Min(buffer.Length, sequence.Length);
                sequence.Slice(0, n).CopyTo(buffer.Span);
                _reader.AdvanceTo(sequence.GetPosition(n));
                return n;
            }

            if (result.IsCompleted || result.IsCanceled)
            {
                _reader.AdvanceTo(sequence.End);
                Interlocked.Exchange(ref _readCompleted, 1);
                return 0;
            }

            _reader.AdvanceTo(sequence.Start, sequence.End);
        }
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref _writeCompleted) != 0) throw new IOException("mux stream writing already completed");
        if (buffer.Length == 0) return;
        FlushResult flush;
        try
        {
            _writer.Write(buffer.Span);
            flush = await _writer.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException e)
        {
            throw new IOException("mux stream write failed", e);
        }
        if (flush.IsCompleted) throw new IOException("remote side closed the mux stream");
        if (flush.IsCanceled) cancellationToken.ThrowIfCancellationRequested();
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override void Write(byte[] buffer, int offset, int count) => WriteAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();

    public override void Flush() { }
    public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    public ValueTask CompleteWritingAsync(CancellationToken ct)
    {
        CompleteWriting();
        return ValueTask.CompletedTask;
    }

    private void CompleteWriting()
    {
        if (Interlocked.Exchange(ref _writeCompleted, 1) == 0)
        {
            try { _writer.Complete(); } catch (InvalidOperationException) { /* already complete */ }
        }
    }

    private void CompleteReading()
    {
        if (Interlocked.Exchange(ref _readCompleted, 1) == 0)
        {
            try { _reader.Complete(); } catch (InvalidOperationException) { /* already complete */ }
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            CompleteWriting();
            CompleteReading();
            if (_completion is not null && _breakGlass is not null)
            {
                var breakGlass = _breakGlass;
                _ = _completion.WaitAsync(GracefulCompletion).ContinueWith(t =>
                {
                    if (!t.IsCompletedSuccessfully) { try { breakGlass(); } catch { /* ignore */ } }
                }, TaskScheduler.Default);
            }
            else
            {
                try { _breakGlass?.Invoke(); } catch { /* ignore */ }
            }
            _onDispose?.Invoke();
        }
        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            CompleteWriting();
            CompleteReading();
            if (_completion is not null)
            {
                try { await _completion.WaitAsync(GracefulCompletion).ConfigureAwait(false); }
                catch { try { _breakGlass?.Invoke(); } catch { /* ignore */ } }
            }
            else
            {
                try { _breakGlass?.Invoke(); } catch { /* ignore */ }
            }
            _onDispose?.Invoke();
        }
        await base.DisposeAsync().ConfigureAwait(false);
    }
}
