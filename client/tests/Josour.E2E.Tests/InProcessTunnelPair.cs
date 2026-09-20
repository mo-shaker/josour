using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Josour.Core.Allowlist;
using Josour.Core.Net;
using Josour.Core.Tunnel;
using Josour.Egress;
using Josour.Proxy;
using Josour.Tunnel;
using Josour.Tunnel.Mux;
using Josour.Tunnel.Candidates;
using Josour.Tunnel.Transport;

namespace Josour.E2E.Tests;

/// <summary>
/// Two real <see cref="TunnelSession"/>s (a host + a guest) on loopback inside the process, with all the layers:
/// SymmetricConnector over TLS -> NerdbankMux -> EgressPolicy/OpenHandler on the host -> ConnectProxyServer on the guest.
/// No manual wiring here: everything the test does is what the application will do in week 4 (Prepare, exchange the endpoints, Connect).
///
/// The host is the one that connects at the TCP level (the guest has no candidates for the other side) so the test owns the transport socket and can kill it.
/// The resolver maps site.test/direct.test to 127.0.0.1, and the blocked check is replaced to permit loopback in this test only.
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

    /// <param name="hostAllowlistOverride">A newer list on the host (the version-divergence window, ADR-0004). null = the same list.</param>
    /// <param name="extraEntries">Entries added to both sides' lists (tests that route an extra name through the tunnel).</param>
    /// <param name="hostAddressBlocker">
    /// The blocked check on the host. null = the real policy except loopback (because the origin inside the process lives on 127.0.0.1).
    /// The self-access tests pass <c>IpRangePolicy.IsBlocked</c> itself to prove loopback really is refused.
    /// </param>
    /// <param name="guestAddressBlocker">The same, on the guest's direct path.</param>
    /// <param name="extraHostNames">Extra names in both resolvers: the name -> the addresses.</param>
    /// <param name="guestSystemProxy">
    /// The system proxy on the guest's direct path (plan 8.5). null = no proxy: the test is not affected by the developer machine's settings.
    /// </param>
    public static async Task<InProcessTunnelPair> CreateAsync(
        IEnumerable<string>? hostAllowlistOverride = null,
        TimeSpan? timeout = null,
        IEnumerable<string>? extraEntries = null,
        Func<IPAddress, bool>? hostAddressBlocker = null,
        Func<IPAddress, bool>? guestAddressBlocker = null,
        IReadOnlyDictionary<string, string[]>? extraHostNames = null,
        ISystemProxyResolver? guestSystemProxy = null)
    {
        var origin = new HttpOrigin();
        try
        {
            var extra = extraEntries?.ToArray() ?? Array.Empty<string>();
            var guestAllowlist = AllowlistMatcher.Parse(1, new[] { $"site.test:{origin.Port}" }.Concat(extra));
            var hostAllowlist = hostAllowlistOverride is null
                ? guestAllowlist
                : AllowlistMatcher.Parse(2, hostAllowlistOverride.Concat(extra));

            var sessionId = Guid.NewGuid();
            var secret = RandomNumberGenerator.GetBytes(32);
            var expires = DateTimeOffset.UtcNow.AddMinutes(30);
            var hostTransport = new RecordingTransport();
            OpenHandler? handler = null;
            ConnectProxyServer? proxyServer = null;

            var hostResolver = new StubResolver().Map("site.test", "127.0.0.1");
            var guestResolver = new StubResolver().Map("direct.test", "127.0.0.1").Map("site.test", "127.0.0.1");
            foreach (var (name, addresses) in extraHostNames ?? new Dictionary<string, string[]>())
            {
                hostResolver.Map(name, addresses);
                guestResolver.Map(name, addresses);
            }

            // The default: loopback is permitted (the origin is inside the process), and the rest of IpRangePolicy stays as it is.
            static bool ExceptLoopback(IPAddress a) => !IPAddress.IsLoopback(a) && IpRangePolicy.IsBlocked(a);
            var blocker = hostAddressBlocker ?? ExceptLoopback;
            var guestBlocker = guestAddressBlocker ?? ExceptLoopback;

            var host = new TunnelSession(
                new SessionMaterial(sessionId, TunnelRole.Host, (byte[])secret.Clone(), expires, true, "198.51.100.2"),
                new TunnelSessionOptions
                {
                    Allowlist = hostAllowlist,
                    // A short liveness for the tests: the main death-detection path is EOF, and this is a safety net
                    // that makes the detection deterministic within the test's timeout rather than relying on the default 60 seconds.
                    Mux = TestMux,
                    Resolver = hostResolver,
                    Transport = hostTransport,
                    BindAddress = IPAddress.Loopback,
                    CandidateSource = () => new LoopbackCandidateSource(),
                    OurPublicIp = PeerPublicIp,
                    HostEgress = context =>
                    {
                        var adapter = (EgressTunnelAdapter)EgressTunnelAdapter.Create(context, blocker);
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
                    Resolver = guestResolver,
                    BindAddress = IPAddress.Loopback,
                    CandidateSource = () => new LoopbackCandidateSource(),
                    GuestProxy = context =>
                    {
                        // No system proxy by default: the in-process test is not affected by the developer machine's settings.
                        var adapter = (ProxyTunnelAdapter)ProxyTunnelAdapter.Create(context, null, null, guestBlocker, guestSystemProxy ?? NoSystemProxy.Instance);
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

/// <summary>A single loopback candidate with no network and no UPnP.</summary>
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

/// <summary>A direct transport that keeps the sockets so the test can kill the transport under TLS (a dead tunnel with no GOAWAY).</summary>
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
            try { stream.Dispose(); } catch { /* already killed */ }
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

    /// <summary>Is the port refusing connections now?</summary>
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
