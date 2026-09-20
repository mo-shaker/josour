using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Josour.Core.Tunnel;
using Josour.Tunnel.Mux;
using Josour.Tunnel.Tests.Mux;

namespace Josour.Tunnel.Tests.Perf;

/// <summary>A mux pair over TLS over a simulated link (RTT/bandwidth), as in MuxPair but through the link.</summary>
internal sealed class WanPair : IAsyncDisposable
{
    private WanPair(MuxPair pair, SimulatedLink link, int window)
    {
        Pair = pair;
        Link = link;
        Window = window;
    }

    public MuxPair Pair { get; }
    public SimulatedLink Link { get; }
    public int Window { get; }
    public NerdbankMux Guest => Pair.Guest;
    public NerdbankMux Host => Pair.Host;

    public const int DefaultWindow = 1024 * 1024;

    public static async Task<WanPair> CreateAsync(LinkProfile profile, int window = DefaultWindow, int seed = 20260905)
    {
        var link = new SimulatedLink(profile, seed);
        var options = new MuxOptions
        {
            EnableLiveness = false,
            ReceiveWindow = window,
            OpenTimeout = TimeSpan.FromSeconds(60),
        };
        var pair = await MuxPair.CreateAsync(
            guestOptions: options,
            hostOptions: options,
            wrapGuest: link.WrapA,
            wrapHost: link.WrapB,
            handshakeTimeout: TimeSpan.FromSeconds(60));
        return new WanPair(pair, link, window);
    }

    /// <summary>The effective RTT as PING measures it through the simulator (better than the nominal one: it includes the timers' error and TLS).</summary>
    public async Task<TimeSpan> MeasureRttAsync(int samples = 5)
    {
        var best = TimeSpan.MaxValue;
        var total = TimeSpan.Zero;
        for (var i = 0; i < samples; i++)
        {
            var rtt = await Guest.PingAsync(CancellationToken.None);
            if (rtt < best) best = rtt;
            total += rtt;
        }
        return total / samples;
    }

    /// <summary>The theoretical ceiling for one stream = the window ÷ the RTT (bytes/s).</summary>
    public static double CeilingBytesPerSecond(int window, TimeSpan rtt) => window / rtt.TotalSeconds;

    public async ValueTask DisposeAsync() => await Pair.DisposeAsync();
}

/// <summary>
/// A simple "origin" on the host's side that speaks a fixed-length request/reply protocol, and lives on the same connection for several requests
/// (the equivalent of keep-alive): the client writes 8 big-endian bytes with the requested reply's length, and the server writes exactly that many.
/// A request of length 0 = a close.
/// </summary>
internal static class ResourceProtocol
{
    public const int RequestBytes = 8;

    public static async Task RequestAsync(Stream stream, long length, CancellationToken ct)
    {
        var head = new byte[RequestBytes];
        BinaryPrimitives.WriteInt64BigEndian(head, length);
        await stream.WriteAsync(head, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    public static async Task ReadExactAsync(Stream stream, long length, CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        long remaining = length;
        while (remaining > 0)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), ct).ConfigureAwait(false);
            if (n == 0) throw new EndOfStreamException($"origin closed with {remaining} bytes of {length} outstanding");
            remaining -= n;
        }
    }

    /// <summary>It serves requests on a stream until EOF. Used on the host's side (the mux's destination) and on the direct path.</summary>
    public static async Task ServeAsync(Stream stream, CancellationToken ct)
    {
        var head = new byte[RequestBytes];
        var chunk = new byte[64 * 1024];
        chunk.AsSpan().Fill((byte)'r');
        while (true)
        {
            var filled = 0;
            while (filled < RequestBytes)
            {
                int n;
                try { n = await stream.ReadAsync(head.AsMemory(filled), ct).ConfigureAwait(false); }
                catch (Exception) { return; }
                if (n == 0) return;
                filled += n;
            }
            var length = BinaryPrimitives.ReadInt64BigEndian(head);
            if (length <= 0) return;
            long sent = 0;
            while (sent < length)
            {
                var n = (int)Math.Min(chunk.Length, length - sent);
                await stream.WriteAsync(chunk.AsMemory(0, n), ct).ConfigureAwait(false);
                sent += n;
            }
            await stream.FlushAsync(ct).ConfigureAwait(false);
        }
    }
}

/// <summary>
/// A destination on the host's side that speaks <see cref="ResourceProtocol"/> over two internal pipes: the mux pumps into it
/// (StreamPump) and it replies. There are no sockets and no network behind it; the origin is "instant".
/// </summary>
internal sealed class ResourceTarget : Stream, IHalfClosable
{
    private readonly System.IO.Pipelines.Pipe _toServer = new();
    private readonly System.IO.Pipelines.Pipe _fromServer = new();
    private readonly Task _server;
    private readonly CancellationTokenSource _cts = new();
    private int _disposed;

    public ResourceTarget()
    {
        var serverSide = new PipeDuplexStream(_toServer.Reader, _fromServer.Writer, Task.CompletedTask, () => { });
        _server = Task.Run(async () =>
        {
            try { await ResourceProtocol.ServeAsync(serverSide, _cts.Token).ConfigureAwait(false); }
            catch (Exception) { /* the channel was closed */ }
            finally
            {
                _fromServer.Writer.Complete();
                _toServer.Reader.Complete();
            }
        });
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        await _toServer.Writer.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        while (true)
        {
            var result = await _fromServer.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            var sequence = result.Buffer;
            if (sequence.Length > 0)
            {
                var n = (int)Math.Min(sequence.Length, buffer.Length);
                sequence.Slice(0, n).CopyTo(buffer.Span);
                _fromServer.Reader.AdvanceTo(sequence.GetPosition(n));
                return n;
            }
            _fromServer.Reader.AdvanceTo(sequence.End);
            if (result.IsCompleted || result.IsCanceled) return 0;
        }
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public ValueTask CompleteWritingAsync(CancellationToken ct)
    {
        _toServer.Writer.Complete();
        return ValueTask.CompletedTask;
    }

    public override void Flush() { }
    public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        // The mux owns the destination and disposes of it; the test may dispose of it too. The disposal is idempotent.
        if (disposing && Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _cts.Cancel();
            _toServer.Writer.Complete();
            _fromServer.Reader.Complete();
            try { _server.Wait(TimeSpan.FromSeconds(1)); } catch { /* ignore */ }
            _cts.Dispose();
        }
        base.Dispose(disposing);
    }
}

/// <summary>
/// A factory of direct connections sharing one wire in each direction: the baseline for comparing "the same fetch, directly",
/// at the same RTT and with the same bottleneck. Every connection pays one RTT for the TCP handshake before its first request.
/// </summary>
internal sealed class DirectLinkFactory : IAsyncDisposable
{
    private readonly LinkMedium _clientToServer;
    private readonly LinkMedium _serverToClient;
    private readonly List<Stream> _owned = new();
    private readonly List<Task> _servers = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly object _gate = new();

    public DirectLinkFactory(LinkProfile profile, int seed = 20260905)
    {
        Profile = profile;
        _clientToServer = new LinkMedium(profile, seed);
        _serverToClient = new LinkMedium(profile, seed + 1);
    }

    public LinkProfile Profile { get; }

    /// <summary>A new connection to an "origin" speaking ResourceProtocol, after paying the TCP handshake's RTT.</summary>
    public async Task<Stream> ConnectAsync(CancellationToken ct)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        Stream client;
        Stream server;
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var accept = listener.AcceptSocketAsync(ct);
            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            await socket.ConnectAsync(IPAddress.Loopback, port, ct).ConfigureAwait(false);
            var far = await accept.ConfigureAwait(false);
            client = new LatencyStream(new NetworkStream(socket, ownsSocket: true), _clientToServer);
            server = new LatencyStream(new NetworkStream(far, ownsSocket: true), _serverToClient);
        }
        finally
        {
            listener.Stop();
        }

        var serverTask = Task.Run(async () =>
        {
            try { await ResourceProtocol.ServeAsync(server, _cts.Token).ConfigureAwait(false); }
            catch (Exception) { /* the connection was closed */ }
        }, CancellationToken.None);

        lock (_gate)
        {
            _owned.Add(client);
            _owned.Add(server);
            _servers.Add(serverTask);
        }

        // The TCP handshake = one RTT before the client can send its first byte.
        await Task.Delay(Profile.Rtt, ct).ConfigureAwait(false);
        return client;
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        Stream[] owned;
        lock (_gate) owned = _owned.ToArray();
        foreach (var item in owned)
        {
            try { await item.DisposeAsync().ConfigureAwait(false); } catch { /* ignore */ }
        }
        _cts.Dispose();
    }
}

/// <summary>
/// A destination producing at a constant bit rate (the equivalent of a video stream): it hands over a chunk every <see cref="ChunkInterval"/> until the duration elapses, then EOF.
/// It swallows whatever is written to it.
/// </summary>
internal sealed class PacedTarget : Stream, IHalfClosable
{
    public static readonly TimeSpan ChunkInterval = TimeSpan.FromMilliseconds(100);

    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly long _bitsPerSecond;
    private readonly TimeSpan _duration;
    private long _produced;

    public PacedTarget(long bitsPerSecond, TimeSpan duration)
    {
        _bitsPerSecond = bitsPerSecond;
        _duration = duration;
    }

    public long TotalBytes => (long)(_bitsPerSecond / 8.0 * _duration.TotalSeconds);
    public long Produced => Volatile.Read(ref _produced);

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        while (true)
        {
            if (_produced >= TotalBytes) return 0;
            var dueBytes = (long)(_bitsPerSecond / 8.0 * _clock.Elapsed.TotalSeconds);
            var available = Math.Min(dueBytes, TotalBytes) - _produced;
            if (available > 0)
            {
                var n = (int)Math.Min(available, buffer.Length);
                buffer.Span[..n].Fill((byte)'v');
                _produced += n;
                return n;
            }
            await Task.Delay(ChunkInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => Task.CompletedTask;
    public ValueTask CompleteWritingAsync(CancellationToken ct) => ValueTask.CompletedTask;
    public override void Flush() { }
    public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}

/// <summary>A heavy page's profile: one document then subresources of mixed sizes (roughly the median of an HTTP archive).</summary>
internal static class PageProfile
{
    public const long DocumentBytes = 60 * 1024;

    /// <summary>80 subresources: 8 large scripts, 6 stylesheets, 40 images, 20 fonts/icons, 6 XHR.</summary>
    public static IReadOnlyList<long> SubResources { get; } = Build();

    public static long TotalBytes => DocumentBytes + SubResources.Sum();

    private static long[] Build()
    {
        var list = new List<long>();
        for (var i = 0; i < 8; i++) list.Add(120 * 1024);
        for (var i = 0; i < 6; i++) list.Add(40 * 1024);
        for (var i = 0; i < 40; i++) list.Add(25 * 1024);
        for (var i = 0; i < 20; i++) list.Add(12 * 1024);
        for (var i = 0; i < 6; i++) list.Add(8 * 1024);
        // An interleaved order: the browser does not fetch resources sorted by size.
        var ordered = new long[list.Count];
        var lo = 0;
        var hi = list.Count - 1;
        for (var i = 0; i < list.Count; i++) ordered[i] = i % 2 == 0 ? list[lo++] : list[hi--];
        return ordered;
    }

    /// <summary>It spreads the resources over <paramref name="lanes"/> connections in turn (as the browser does with keep-alive).</summary>
    public static List<long>[] Split(int lanes)
    {
        var result = new List<long>[lanes];
        for (var i = 0; i < lanes; i++) result[i] = new List<long>();
        for (var i = 0; i < SubResources.Count; i++) result[i % lanes].Add(SubResources[i]);
        return result;
    }
}

internal static class Perf
{
    public const double MB = 1_000_000;

    public static string Rate(long bytes, TimeSpan elapsed)
    {
        var mbps = bytes / MB / elapsed.TotalSeconds;
        return $"{mbps:F2} MB/s ({mbps * 8:F1} Mbit/s)";
    }

    public static double Percentile(IReadOnlyList<double> sorted, double p)
    {
        if (sorted.Count == 0) return 0;
        var index = (int)Math.Round((sorted.Count - 1) * p, MidpointRounding.AwayFromZero);
        return sorted[Math.Clamp(index, 0, sorted.Count - 1)];
    }

    /// <summary>Settled managed memory after a full collection.</summary>
    public static long SettledMemory()
    {
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        return GC.GetTotalMemory(forceFullCollection: true);
    }

    public static Stopwatch Start() => Stopwatch.StartNew();
}
