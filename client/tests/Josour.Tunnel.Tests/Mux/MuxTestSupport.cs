using System.Net;
using System.Net.Sockets;
using Josour.Core.Tunnel;
using Josour.Tunnel.Certificates;
using Josour.Tunnel.Mux;
using Josour.Tunnel.Tls;

namespace Josour.Tunnel.Tests.Mux;

/// <summary>A mux pair over real TLS on loopback (as SymmetricConnector produces it): the guest is the TLS client, the host is the TLS server.</summary>
internal sealed class MuxPair : IAsyncDisposable
{
    private readonly SessionCertificate _cert;

    private MuxPair(NerdbankMux guest, NerdbankMux host, SessionCertificate cert, BlackholeStream? guestRaw, string tlsVersion)
    {
        Guest = guest;
        Host = host;
        _cert = cert;
        GuestRaw = guestRaw;
        TlsVersion = tlsVersion;
    }

    public NerdbankMux Guest { get; }
    public NerdbankMux Host { get; }
    /// <summary>The negotiated version ("1.2"/"1.3"); recorded with the performance numbers.</summary>
    public string TlsVersion { get; }
    /// <summary>The raw stream of the guest's side (before TLS) if control over it is wanted.</summary>
    public BlackholeStream? GuestRaw { get; }

    /// <param name="wrapGuest">An optional wrapper over the guest side's raw transport before TLS (the link simulator in the performance tests).</param>
    /// <param name="wrapHost">The same for the host's side.</param>
    /// <param name="handshakeTimeout">The TLS handshake timeout; it is raised when there is a simulated RTT.</param>
    public static async Task<MuxPair> CreateAsync(
        MuxOptions? guestOptions = null,
        MuxOptions? hostOptions = null,
        bool blackholeGuest = false,
        Func<Stream, Stream>? wrapGuest = null,
        Func<Stream, Stream>? wrapHost = null,
        TimeSpan? handshakeTimeout = null)
    {
        var cert = SessionCertificate.Create(DateTimeOffset.UtcNow.AddMinutes(30));
        var (a, b) = await Loopback.CreatePairAsync();
        BlackholeStream? guestRaw = null;
        Stream guestSide = a;
        if (blackholeGuest) guestSide = guestRaw = new BlackholeStream(a);
        if (wrapGuest is not null) guestSide = wrapGuest(guestSide);
        if (wrapHost is not null) b = wrapHost(b);
        var timeout = handshakeTimeout ?? TimeSpan.FromSeconds(10);
        var serverTask = TlsChannel.AuthenticateAsServerAsync(b, cert.Certificate, timeout, CancellationToken.None);
        var clientTask = TlsChannel.AuthenticateAsClientAsync(guestSide, cert.FingerprintSha256, timeout, CancellationToken.None);
        var server = await serverTask;
        var client = await clientTask;
        var guest = NerdbankMux.Create(client.Stream, TunnelRole.Guest, guestOptions ?? new MuxOptions { EnableLiveness = false });
        var host = NerdbankMux.Create(server.Stream, TunnelRole.Host, hostOptions ?? new MuxOptions { EnableLiveness = false });
        return new MuxPair(guest, host, cert, guestRaw, server.TlsVersion);
    }

    public async ValueTask DisposeAsync()
    {
        await Guest.DisposeAsync();
        await Host.DisposeAsync();
        _cert.Dispose();
    }
}

/// <summary>
/// A test destination on the host: it swallows the writes (after an optional gate). If produce > 0 it produces that much on read and then EOF
/// (a server that closes after replying); otherwise it gives EOF when the half-close reaches it (a server that closes when the request ends).
/// </summary>
internal sealed class TestTarget : Stream, IHalfClosable
{
    private readonly TaskCompletionSource _eof = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly long _produce;
    private long _produced;
    private long _received;

    public TestTarget(long produce = 0, TaskCompletionSource? gate = null)
    {
        _produce = produce;
        Gate = gate;
    }

    public TaskCompletionSource? Gate { get; }
    public long Received => Volatile.Read(ref _received);
    /// <summary>What it actually handed over to be read (it stops at the other side's receiving window if it is not reading).</summary>
    public long Produced => Volatile.Read(ref _produced);
    public bool PeerHalfClosed { get; private set; }
    public bool Disposed { get; private set; }
    public TaskCompletionSource DisposedSignal { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (Gate is not null) await Gate.Task.WaitAsync(cancellationToken);
        Interlocked.Add(ref _received, buffer.Length);
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var remaining = _produce - _produced;
        if (remaining > 0)
        {
            var n = (int)Math.Min(remaining, buffer.Length);
            buffer.Span[..n].Fill((byte)'x');
            _produced += n;
            return n;
        }
        if (_produce > 0) return 0;
        await _eof.Task.WaitAsync(cancellationToken);
        return 0;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public ValueTask CompleteWritingAsync(CancellationToken ct)
    {
        PeerHalfClosed = true;
        _eof.TrySetResult();
        return ValueTask.CompletedTask;
    }

    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        Disposed = true;
        _eof.TrySetResult();
        DisposedSignal.TrySetResult();
        base.Dispose(disposing);
    }
}

/// <summary>A wrapper that can swallow all the traffic silently (to simulate a dead tunnel with no TCP close).</summary>
internal sealed class BlackholeStream : Stream
{
    private readonly Stream _inner;
    private readonly TaskCompletionSource _never = new();
    private volatile bool _blackhole;

    public BlackholeStream(Stream inner) => _inner = inner;

    public void Blackhole() => _blackhole = true;

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        while (true)
        {
            var n = await _inner.ReadAsync(buffer, cancellationToken);
            if (!_blackhole) return n;
            if (n == 0) { await _never.Task.WaitAsync(cancellationToken); }
        }
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        => _blackhole ? ValueTask.CompletedTask : _inner.WriteAsync(buffer, cancellationToken);

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override void Flush() { }
    public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();
    public override void Write(byte[] buffer, int offset, int count) => WriteAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing) _inner.Dispose();
        base.Dispose(disposing);
    }
}

/// <summary>A real TCP socket as a destination: it returns a SocketStream for the host's side and the far socket for the test.</summary>
internal static class TcpTarget
{
    public static async Task<(SocketStream Target, Socket Far)> CreateAsync()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var acceptTask = listener.AcceptSocketAsync();
            var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            await client.ConnectAsync(IPAddress.Loopback, port);
            var far = await acceptTask;
            return (new SocketStream(client), far);
        }
        finally
        {
            listener.Stop();
        }
    }
}

internal static class StreamIo
{
    public static async Task WriteAllAsync(Stream stream, long bytes, int chunk = 64 * 1024, CancellationToken ct = default)
    {
        var buffer = new byte[chunk];
        buffer.AsSpan().Fill((byte)'y');
        long sent = 0;
        while (sent < bytes)
        {
            var n = (int)Math.Min(chunk, bytes - sent);
            await stream.WriteAsync(buffer.AsMemory(0, n), ct);
            sent += n;
        }
    }

    public static async Task<long> ReadToEndAsync(Stream stream, CancellationToken ct = default)
    {
        var buffer = new byte[64 * 1024];
        long total = 0;
        while (true)
        {
            var n = await stream.ReadAsync(buffer, ct);
            if (n == 0) return total;
            total += n;
        }
    }

    public static async Task<byte[]> ReadExactAsync(Stream stream, int count, CancellationToken ct = default)
    {
        var buffer = new byte[count];
        await stream.ReadExactlyAsync(buffer, ct);
        return buffer;
    }
}
