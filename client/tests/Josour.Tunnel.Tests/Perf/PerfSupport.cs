using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Josour.Core.Tunnel;
using Josour.Tunnel.Mux;
using Josour.Tunnel.Tests.Mux;

namespace Josour.Tunnel.Tests.Perf;

/// <summary>زوج Mux فوق TLS فوق وصلة مُحاكاة (RTT/نطاق)، كما في MuxPair لكن عبر الوصلة.</summary>
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

    /// <summary>RTT الفعلي كما يقيسه PING عبر المحاكي (أفضل من الاسمي: يشمل خطأ المؤقّتات وTLS).</summary>
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

    /// <summary>السقف النظري لـ stream واحد = النافذة ÷ RTT (بايت/ث).</summary>
    public static double CeilingBytesPerSecond(int window, TimeSpan rtt) => window / rtt.TotalSeconds;

    public async ValueTask DisposeAsync() => await Pair.DisposeAsync();
}

/// <summary>
/// «أصل» بسيط على جانب المضيف يتكلم بروتوكول طلب/رد ثابت الطول، ويعيش على نفس الاتصال لعدة طلبات
/// (نظير keep-alive): العميل يكتب 8 بايت big-endian بطول الرد المطلوب، والخادم يكتب هذا العدد بالضبط.
/// طلب بطول 0 = إغلاق.
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

    /// <summary>يخدم الطلبات على stream حتى EOF. يُستعمل على جانب المضيف (وجهة الـ mux) وعلى المسار المباشر.</summary>
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
/// وجهة على جانب المضيف تتكلم <see cref="ResourceProtocol"/> فوق أنبوبين داخليين: الـ mux يضخ فيها
/// (StreamPump) وهي ترد. لا مقابس ولا شبكة خلفها؛ الأصل «فوري».
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
            catch (Exception) { /* أُغلقت القناة */ }
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
        // الـ Mux يملك الوجهة ويتخلص منها؛ الاختبار قد يتخلص منها أيضًا. التخلص عديم التكرار.
        if (disposing && Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _cts.Cancel();
            _toServer.Writer.Complete();
            _fromServer.Reader.Complete();
            try { _server.Wait(TimeSpan.FromSeconds(1)); } catch { /* تجاهل */ }
            _cts.Dispose();
        }
        base.Dispose(disposing);
    }
}

/// <summary>
/// مصنع اتصالات مباشرة تتشارك سلكًا واحدًا في كل اتجاه: خط الأساس لمقارنة «نفس الجلب مباشرةً»،
/// بنفس الـ RTT ونفس عنق الزجاجة. كل اتصال يدفع RTT واحدًا لمصافحة TCP قبل أول طلب.
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

    /// <summary>اتصال جديد إلى «أصل» يتكلم ResourceProtocol، بعد دفع RTT مصافحة TCP.</summary>
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
            catch (Exception) { /* الاتصال أُغلق */ }
        }, CancellationToken.None);

        lock (_gate)
        {
            _owned.Add(client);
            _owned.Add(server);
            _servers.Add(serverTask);
        }

        // مصافحة TCP = RTT واحد قبل أن يستطيع العميل إرسال أول بايت.
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
            try { await item.DisposeAsync().ConfigureAwait(false); } catch { /* تجاهل */ }
        }
        _cts.Dispose();
    }
}

/// <summary>
/// وجهة تنتج بمعدل بت ثابت (نظير بث فيديو): تسلّم قطعة كل <see cref="ChunkInterval"/> حتى تنقضي المدة ثم EOF.
/// تبتلع ما يُكتب إليها.
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

/// <summary>ملف صفحة ثقيلة: مستند واحد ثم موارد فرعية بأحجام مختلطة (وسيط أرشيف HTTP تقريبًا).</summary>
internal static class PageProfile
{
    public const long DocumentBytes = 60 * 1024;

    /// <summary>80 موردًا فرعيًا: 8 سكربتات كبيرة، 6 أنماط، 40 صورة، 20 خطًا/أيقونة، 6 XHR.</summary>
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
        // ترتيب متناوب: المتصفح لا يجلب الموارد مرتبة بالحجم.
        var ordered = new long[list.Count];
        var lo = 0;
        var hi = list.Count - 1;
        for (var i = 0; i < list.Count; i++) ordered[i] = i % 2 == 0 ? list[lo++] : list[hi--];
        return ordered;
    }

    /// <summary>يوزّع الموارد على <paramref name="lanes"/> اتصالًا بالتناوب (كما يفعل المتصفح مع keep-alive).</summary>
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

    /// <summary>ذاكرة مُدارة مستقرة بعد جمع كامل.</summary>
    public static long SettledMemory()
    {
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        return GC.GetTotalMemory(forceFullCollection: true);
    }

    public static Stopwatch Start() => Stopwatch.StartNew();
}
