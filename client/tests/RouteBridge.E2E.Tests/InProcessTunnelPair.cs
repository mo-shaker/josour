using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using RouteBridge.Core.Allowlist;
using RouteBridge.Core.Net;
using RouteBridge.Core.Tunnel;
using RouteBridge.Egress;
using RouteBridge.Proxy;
using RouteBridge.Tunnel;
using RouteBridge.Tunnel.Mux;
using RouteBridge.Tunnel.Candidates;
using RouteBridge.Tunnel.Transport;

namespace RouteBridge.E2E.Tests;

/// <summary>
/// جلستا <see cref="TunnelSession"/> حقيقيتان (مضيف + ضيف) على loopback داخل العملية، بكل الطبقات:
/// SymmetricConnector فوق TLS → NerdbankMux → EgressPolicy/OpenHandler على المضيف → ConnectProxyServer على الضيف.
/// لا ربط يدوي هنا: كل ما يفعله الاختبار هو ما سيفعله التطبيق في الأسبوع 4 (Prepare، تبادل النهايات، Connect).
///
/// المضيف هو من يتصل على مستوى TCP (الضيف بلا مرشحين للطرف الآخر) ليملك الاختبار مقبس النقل ويستطيع قتله.
/// المحلل يربط site.test/direct.test بـ 127.0.0.1، وفحص الحظر يُستبدل للسماح بـ loopback في هذا الاختبار فقط.
/// </summary>
internal sealed class InProcessTunnelPair : IAsyncDisposable
{
    private static MuxOptions TestMux => new() { PingInterval = TimeSpan.FromSeconds(1), DeadAfter = TimeSpan.FromSeconds(6) };

    public const string PeerPublicIp = "203.0.113.7";

    private InProcessTunnelPair(TunnelSession host, TunnelSession guest, HttpOrigin origin, RecordingTransport hostTransport)
    {
        Host = host;
        Guest = guest;
        Origin = origin;
        HostTransport = hostTransport;
    }

    public TunnelSession Host { get; }
    public TunnelSession Guest { get; }
    public HttpOrigin Origin { get; }
    public RecordingTransport HostTransport { get; }
    public OpenHandler HostHandler { get; private set; } = null!;
    public ConnectProxyServer ProxyServer { get; private set; } = null!;
    public TunnelConnectResult HostResult { get; private set; } = null!;
    public TunnelConnectResult GuestResult { get; private set; } = null!;

    public int ProxyPort => Guest.Proxy!.Port;
    public int OriginPort => Origin.Port;

    /// <param name="hostAllowlistOverride">قائمة أحدث على المضيف (نافذة تباين الإصدار، ADR-0004). null = القائمة نفسها.</param>
    public static async Task<InProcessTunnelPair> CreateAsync(IEnumerable<string>? hostAllowlistOverride = null, TimeSpan? timeout = null)
    {
        var origin = new HttpOrigin();
        try
        {
            var guestAllowlist = AllowlistMatcher.Parse(1, new[] { $"site.test:{origin.Port}" });
            var hostAllowlist = hostAllowlistOverride is null
                ? guestAllowlist
                : AllowlistMatcher.Parse(2, hostAllowlistOverride);

            var sessionId = Guid.NewGuid();
            var secret = RandomNumberGenerator.GetBytes(32);
            var expires = DateTimeOffset.UtcNow.AddMinutes(30);
            var hostTransport = new RecordingTransport();
            OpenHandler? handler = null;
            ConnectProxyServer? proxyServer = null;

            var host = new TunnelSession(
                new SessionMaterial(sessionId, TunnelRole.Host, (byte[])secret.Clone(), expires, true, "198.51.100.2"),
                new TunnelSessionOptions
                {
                    Allowlist = hostAllowlist,
                    // حيوية قصيرة للاختبارات: المسار الأساسي لكشف الموت هو EOF، وهذه شبكة أمان
                    // تجعل الكشف حتميًا داخل مهلة الاختبار بدل الاعتماد على 60 ثانية الافتراضية.
                    Mux = TestMux,
                    Resolver = new StubResolver().Map("site.test", "127.0.0.1"),
                    Transport = hostTransport,
                    BindAddress = IPAddress.Loopback,
                    CandidateSource = () => new LoopbackCandidateSource(),
                    OurPublicIp = PeerPublicIp,
                    HostEgress = context =>
                    {
                        // loopback مسموح في هذا الاختبار فقط؛ بقية سياسة IpRangePolicy كما هي.
                        var adapter = (EgressTunnelAdapter)EgressTunnelAdapter.Create(
                            context, a => !IPAddress.IsLoopback(a) && IpRangePolicy.IsBlocked(a));
                        handler = adapter.Handler;
                        return adapter;
                    },
                });

            var guest = new TunnelSession(
                new SessionMaterial(sessionId, TunnelRole.Guest, (byte[])secret.Clone(), expires, true, PeerPublicIp),
                new TunnelSessionOptions
                {
                    Allowlist = guestAllowlist,
                    Mux = TestMux,
                    Resolver = new StubResolver().Map("direct.test", "127.0.0.1").Map("site.test", "127.0.0.1"),
                    BindAddress = IPAddress.Loopback,
                    CandidateSource = () => new LoopbackCandidateSource(),
                    GuestProxy = context =>
                    {
                        var adapter = (ProxyTunnelAdapter)ProxyTunnelAdapter.Create(context);
                        proxyServer = adapter.Server;
                        return adapter;
                    },
                });

            var window = timeout ?? TimeSpan.FromSeconds(20);
            var hostLocal = await host.PrepareAsync(CancellationToken.None);
            var guestLocal = await guest.PrepareAsync(CancellationToken.None);
            var hostTask = host.ConnectAsync(new PeerEndpointInfo(guestLocal.CertFingerprintSha256Hex, guestLocal.Candidates), window, CancellationToken.None);
            var guestTask = guest.ConnectAsync(new PeerEndpointInfo(hostLocal.CertFingerprintSha256Hex, Array.Empty<CandidateEndpoint>()), window, CancellationToken.None);

            var pair = new InProcessTunnelPair(host, guest, origin, hostTransport)
            {
                HostResult = await hostTask,
                GuestResult = await guestTask,
            };
            if (!pair.HostResult.Connected || !pair.GuestResult.Connected)
                throw new InvalidOperationException($"tunnel sessions did not connect: {pair.HostResult.FailureReason}/{pair.GuestResult.FailureReason}");
            pair.HostHandler = handler ?? throw new InvalidOperationException("host egress was never created");
            pair.ProxyServer = proxyServer ?? throw new InvalidOperationException("guest proxy was never created");
            return pair;
        }
        catch
        {
            origin.Dispose();
            throw;
        }
    }

    public HttpClient NewProxiedClient(TimeSpan? timeout = null)
    {
        var handler = new HttpClientHandler { Proxy = new WebProxy($"http://127.0.0.1:{ProxyPort}"), UseProxy = true };
        return new HttpClient(handler) { Timeout = timeout ?? TimeSpan.FromSeconds(20) };
    }

    public async Task<TcpClient> ConnectToProxyAsync()
    {
        var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, ProxyPort);
        return client;
    }

    public async ValueTask DisposeAsync()
    {
        await Guest.DisposeAsync();
        await Host.DisposeAsync();
        Origin.Dispose();
    }
}

/// <summary>مرشح loopback واحد بلا شبكة ولا UPnP.</summary>
internal sealed class LoopbackCandidateSource : ICandidateSource
{
    public bool HasMapping => false;

    public Task<GatherResult> GatherAsync(GatherOptions options, CancellationToken ct)
        => Task.FromResult(new GatherResult(
            new[] { new CandidateEndpoint(CandidateType.Lan, "127.0.0.1", options.ListenPort) },
            new GatherDiagnostics(false, false, null, false, false, Array.Empty<string>())));

    public Task<bool> RemoveMappingAsync(CancellationToken ct) => Task.FromResult(false);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>نقل مباشر يحتفظ بالمقابس ليستطيع الاختبار قتل النقل تحت TLS (نفق ميت بلا GOAWAY).</summary>
internal sealed class RecordingTransport : ITunnelTransport
{
    private readonly ITunnelTransport _inner = new DirectTransport();
    private readonly List<Stream> _streams = new();
    private readonly object _gate = new();

    public string Name => "recording";

    public async Task<Stream> ConnectAsync(CandidateEndpoint endpoint, TimeSpan timeout, CancellationToken ct)
    {
        var stream = await _inner.ConnectAsync(endpoint, timeout, ct);
        lock (_gate) _streams.Add(stream);
        return stream;
    }

    public void KillAll()
    {
        Stream[] streams;
        lock (_gate) streams = _streams.ToArray();
        foreach (var stream in streams)
        {
            try { stream.Dispose(); } catch { /* مقتول أصلًا */ }
        }
    }
}

internal static class E2EWait
{
    public static async Task<bool> UntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return true;
            await Task.Delay(20);
        }
        return condition();
    }

    /// <summary>هل المنفذ يرفض الاتصال الآن؟</summary>
    public static async Task<bool> PortRefusesAsync(int port)
    {
        using var client = new TcpClient();
        try
        {
            await client.ConnectAsync(IPAddress.Loopback, port).WaitAsync(TimeSpan.FromSeconds(5));
            return false;
        }
        catch (SocketException) { return true; }
        catch (TimeoutException) { return false; }
    }

    public static byte[] Ascii(string text) => Encoding.ASCII.GetBytes(text);
}
