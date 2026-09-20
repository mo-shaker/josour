using System.Net;
using Josour.Core.Tunnel;
using Josour.Tunnel.Candidates;
using Josour.Tunnel.Mux;
using Josour.Tunnel.Transport;

namespace Josour.Tunnel.Tests;

/// <summary>A fixed candidate source with no network and no UPnP: it gives lan 127.0.0.1:&lt;the listener's port&gt; and counts the mapping-removal calls.</summary>
internal sealed class StaticCandidateSource : ICandidateSource
{
    private readonly bool _withCandidate;

    public StaticCandidateSource(bool withCandidate = true, bool hasMapping = false)
    {
        _withCandidate = withCandidate;
        HasMapping = hasMapping;
    }

    public int GatherCalls { get; private set; }
    public GatherOptions? LastOptions { get; private set; }
    public bool HasMapping { get; private set; }
    public int RemoveMappingCalls { get; private set; }
    public bool Disposed { get; private set; }

    public Task<GatherResult> GatherAsync(GatherOptions options, CancellationToken ct)
    {
        GatherCalls++;
        LastOptions = options;
        var candidates = _withCandidate
            ? new[] { new CandidateEndpoint(CandidateType.Lan, "127.0.0.1", options.ListenPort) }
            : Array.Empty<CandidateEndpoint>();
        var diagnostics = new GatherDiagnostics(false, HasMapping, null, false, false, new[] { "static source" });
        return Task.FromResult(new GatherResult(candidates, diagnostics));
    }

    public Task<bool> RemoveMappingAsync(CancellationToken ct)
    {
        RemoveMappingCalls++;
        var had = HasMapping;
        HasMapping = false;
        return Task.FromResult(had);
    }

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        return ValueTask.CompletedTask;
    }
}

/// <summary>A direct transport that keeps the streams it created so the test can kill the transport under TLS (a dead tunnel with no GOAWAY).</summary>
internal sealed class RecordingTransport : ITunnelTransport
{
    private readonly ITunnelTransport _inner = new DirectTransport();
    private readonly List<Stream> _streams = new();
    private readonly object _gate = new();
    private readonly TimeSpan _connectDelay;

    /// <param name="connectDelay">
    /// A delay inside the <c>connect_ms</c> measurement window (after the TCP connect and before TLS): a simulated slow link for testing
    /// the window's derivation from the RTT. Loopback alone gives connect_ms ≈ 0, which is the number the contract counts as corrupt.
    /// </param>
    public RecordingTransport(TimeSpan? connectDelay = null) => _connectDelay = connectDelay ?? TimeSpan.Zero;

    public string Name => "recording";
    public int ConnectCount { get { lock (_gate) return _streams.Count; } }

    public async Task<Stream> ConnectAsync(CandidateEndpoint endpoint, TimeSpan timeout, CancellationToken ct)
    {
        var stream = await _inner.ConnectAsync(endpoint, timeout, ct);
        lock (_gate) _streams.Add(stream);
        if (_connectDelay > TimeSpan.Zero) await Task.Delay(_connectDelay, ct);
        return stream;
    }

    /// <summary>It closes the socket under TLS with no GOAWAY: both sides see the tunnel as dead.</summary>
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

/// <summary>A fake egress policy: it records the wiring and the disposal and returns fixed counts.</summary>
internal sealed class FakeEgress : ITunnelEgress
{
    private readonly List<string> _log;

    public FakeEgress(List<string>? log = null) => _log = log ?? new List<string>();

    public IMuxAcceptor? Attached { get; private set; }
    public bool Disposed { get; private set; }
    public long BytesUp { get; set; } = 11;
    public long BytesDown { get; set; } = 22;
    public List<string> DomainList { get; } = new() { "example.test" };
    public IReadOnlyCollection<string> Domains => DomainList;

    public void Attach(IMuxAcceptor acceptor)
    {
        Attached = acceptor;
        acceptor.OpenRequested = (_, _) => Task.FromResult(MuxOpenDecision.Fail(OpenFailReason.NotAllowed));
    }

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        lock (_log) _log.Add("egress.dispose");
        return ValueTask.CompletedTask;
    }
}

/// <summary>A fake proxy: it records the order of the stopping steps and allows the check-page signal to be raised by hand.</summary>
internal sealed class FakeProxy : ITunnelProxy
{
    public IReadOnlyDictionary<string, long> Counters { get; } = new Dictionary<string, long>(StringComparer.Ordinal);

    private readonly List<string> _log;

    public FakeProxy(List<string>? log = null) => _log = log ?? new List<string>();

    public int Port { get; init; } = 43110;
    public string ProbeUrl => "http://check.josour/";
    public bool Started { get; private set; }
    public bool Disposed { get; private set; }

    public event Action? ProbeSeen;

    public void RaiseProbe() => ProbeSeen?.Invoke();

    public void Start()
    {
        Started = true;
        lock (_log) _log.Add("proxy.start");
    }

    public void StopAccepting()
    {
        lock (_log) _log.Add("proxy.stop_accepting");
    }

    public Task StopAsync()
    {
        lock (_log) _log.Add("proxy.stop");
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        lock (_log) _log.Add("proxy.dispose");
        return ValueTask.CompletedTask;
    }
}

/// <summary>Two sessions (a host + a guest) on loopback inside the process, with fixed candidates and fake pieces. The host is the one that connects.</summary>
internal sealed class TunnelSessionPair : IAsyncDisposable
{
    private TunnelSessionPair(TunnelSession host, TunnelSession guest, FakeEgress egress, FakeProxy proxy, RecordingTransport hostTransport, List<string> log)
    {
        Host = host;
        Guest = guest;
        Egress = egress;
        Proxy = proxy;
        HostTransport = hostTransport;
        Log = log;
    }

    public TunnelSession Host { get; }
    public TunnelSession Guest { get; }
    public FakeEgress Egress { get; }
    public FakeProxy Proxy { get; }
    public RecordingTransport HostTransport { get; }
    public List<string> Log { get; }
    public TunnelConnectResult HostResult { get; private set; } = null!;
    public TunnelConnectResult GuestResult { get; private set; } = null!;
    /// <summary>The context as the host built it for the egress factory (it carries the window-derived stream limit).</summary>
    public TunnelEgressContext? EgressContext { get; private set; }
    /// <summary>The mux as it was passed to the proxy factory on the guest's side (the interface does not expose it).</summary>
    public IMuxConnection? GuestMux { get; private set; }
    public List<TunnelState> HostStates { get; } = new();
    public List<TunnelState> GuestStates { get; } = new();

    public static async Task<TunnelSessionPair> CreateAsync(
        TimeSpan? timeout = null,
        Func<CancellationToken, Task>? guestCloseBrowser = null,
        List<string>? sharedLog = null,
        TimeSpan? connectDelay = null,
        MuxOptions? muxOverride = null)
    {
        var log = sharedLog ?? new List<string>();
        var sessionId = Guid.NewGuid();
        var secret = TestMaterial.NewSecret();
        var expires = DateTimeOffset.UtcNow.AddMinutes(30);
        var egress = new FakeEgress(log);
        var proxy = new FakeProxy(log);
        var hostTransport = new RecordingTransport(connectDelay);
        TunnelEgressContext? egressContext = null;

        var host = new TunnelSession(
            new SessionMaterial(sessionId, TunnelRole.Host, (byte[])secret.Clone(), expires, true, "198.51.100.2"),
            new TunnelSessionOptions
            {
                BindAddress = IPAddress.Loopback,
                CandidateSource = () => new StaticCandidateSource(),
                Transport = hostTransport,
                HostEgress = context => { egressContext = context; return egress; },
                // A short liveness rather than disabling it: the main death-detection path is EOF, and this is a safety net
                // that makes the detection deterministic within the test's timeout instead of waiting out the default 60 seconds.
                Mux = muxOverride ?? new MuxOptions { PingInterval = TimeSpan.FromSeconds(2), DeadAfter = TimeSpan.FromSeconds(8) },
            });
        IMuxConnection? guestMux = null;
        var guest = new TunnelSession(
            new SessionMaterial(sessionId, TunnelRole.Guest, (byte[])secret.Clone(), expires, true, "203.0.113.7"),
            new TunnelSessionOptions
            {
                BindAddress = IPAddress.Loopback,
                CandidateSource = () => new StaticCandidateSource(),
                GuestProxy = ctx => { guestMux = ctx.Mux; return proxy; },
                CloseBrowserAsync = guestCloseBrowser,
                // A short liveness rather than disabling it: the main death-detection path is EOF, and this is a safety net
                // that makes the detection deterministic within the test's timeout instead of waiting out the default 60 seconds.
                Mux = muxOverride ?? new MuxOptions { PingInterval = TimeSpan.FromSeconds(2), DeadAfter = TimeSpan.FromSeconds(8) },
            });

        var pair = new TunnelSessionPair(host, guest, egress, proxy, hostTransport, log);
        host.StateChanged += s => { lock (pair.HostStates) pair.HostStates.Add(s); };
        guest.StateChanged += s => { lock (pair.GuestStates) pair.GuestStates.Add(s); };

        var hostLocal = await host.PrepareAsync(CancellationToken.None);
        var guestLocal = await guest.PrepareAsync(CancellationToken.None);
        var window = timeout ?? TimeSpan.FromSeconds(20);

        // The host is the connector (the guest has no candidates for the other side), so the test owns the transport socket and can kill it.
        var hostTask = host.ConnectAsync(new PeerEndpointInfo(guestLocal.CertFingerprintSha256Hex, guestLocal.Candidates), window, CancellationToken.None);
        var guestTask = guest.ConnectAsync(new PeerEndpointInfo(hostLocal.CertFingerprintSha256Hex, Array.Empty<CandidateEndpoint>()), window, CancellationToken.None);
        pair.HostResult = await hostTask;
        pair.GuestResult = await guestTask;
        pair.GuestMux = guestMux;
        pair.EgressContext = egressContext;
        return pair;
    }

    public async ValueTask DisposeAsync()
    {
        await Guest.DisposeAsync();
        await Host.DisposeAsync();
    }
}

internal static class Wait
{
    /// <summary>It waits for a condition until the timeout (for events arriving from other threads).</summary>
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
}
