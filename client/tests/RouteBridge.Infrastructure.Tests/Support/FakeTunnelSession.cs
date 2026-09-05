using RouteBridge.Core.Tunnel;
using RouteBridge.Infrastructure.Session;

namespace RouteBridge.Infrastructure.Tests.Support;

/// <summary>
/// A scripted <see cref="ITunnelSession"/>: the test decides what <c>PrepareAsync</c> and <c>ConnectAsync</c> return, raises
/// <c>ProbeSeen</c>/<c>Died</c> by hand, and can hold <c>ConnectAsync</c> open to test what happens meanwhile.
/// <c>EndAsync</c> imitates the real cleanup order of docs/protocol.md section 7 in the one respect the app depends on:
/// the browser hook runs inside it (step 2).
/// </summary>
public sealed class FakeTunnelSession : ITunnelSession
{
    private readonly Func<CancellationToken, Task>? _closeBrowser;
    private int _endCount;

    public FakeTunnelSession(SessionMaterial material, Func<CancellationToken, Task>? closeBrowser = null)
    {
        Material = material;
        _closeBrowser = closeBrowser;
        SessionId = material.SessionId;
        Role = material.Role;
    }

    public SessionMaterial Material { get; }

    public Guid SessionId { get; }

    public TunnelRole Role { get; }

    public TunnelState State { get; private set; } = TunnelState.Idle;

    public event Action<TunnelState>? StateChanged;

    public event Action? ProbeSeen;

    public event Action<TunnelEndReason>? Died;

    // ---- script ----

    public LocalEndpointInfo Local { get; set; } = new(
        "9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08",
        new[] { new CandidateEndpoint(CandidateType.Lan, "192.168.1.20", 51234) });

    public TunnelConnectResult ConnectResult { get; set; } = new(true, CandidateType.Lan, 42, "1.3", null);

    /// <summary>Set to hold <c>ConnectAsync</c> until the test releases it.</summary>
    public TaskCompletionSource? ConnectGate { get; set; }

    public GuestProxyInfo? Proxy { get; set; }

    public TunnelStats Stats { get; set; } = new(0, 0, 0);

    public IReadOnlyCollection<string> DomainsSeen { get; set; } = Array.Empty<string>();

    public IReadOnlyDictionary<string, object?> Diagnostics { get; set; } =
        new Dictionary<string, object?>(StringComparer.Ordinal) { ["connect"] = "no candidate answered" };

    // ---- observations ----

    public int PrepareCount { get; private set; }

    public int ConnectCount { get; private set; }

    public int EndCount => Volatile.Read(ref _endCount);

    public TunnelEndReason? EndReason { get; private set; }

    public PeerEndpointInfo? Peer { get; private set; }

    public TimeSpan? ConnectTimeout { get; private set; }

    public bool Disposed { get; private set; }

    public Task<LocalEndpointInfo> PrepareAsync(CancellationToken ct)
    {
        PrepareCount++;
        SetState(TunnelState.Listening);
        return Task.FromResult(Local);
    }

    public async Task<TunnelConnectResult> ConnectAsync(PeerEndpointInfo peer, TimeSpan timeout, CancellationToken ct)
    {
        ConnectCount++;
        Peer = peer;
        ConnectTimeout = timeout;
        SetState(TunnelState.Connecting);
        if (ConnectGate is { } gate)
        {
            await gate.Task.WaitAsync(ct).ConfigureAwait(false);
        }

        if (ConnectResult.Connected)
        {
            SetState(TunnelState.Connected);
        }

        return ConnectResult;
    }

    public async Task EndAsync(TunnelEndReason reason, CancellationToken ct)
    {
        if (Interlocked.Increment(ref _endCount) == 1)
        {
            EndReason = reason;
        }

        // docs/protocol.md section 7 step 2: the app's browser hook runs inside the tunnel's cleanup.
        if (_closeBrowser is not null)
        {
            await _closeBrowser(ct).ConfigureAwait(false);
        }

        SetState(TunnelState.Ended);
    }

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        return ValueTask.CompletedTask;
    }

    /// <summary>The probe page was reached through the local proxy.</summary>
    public void RaiseProbeSeen() => ProbeSeen?.Invoke();

    /// <summary>The tunnel died on its own (peer gone, PONG timeout); the payload is the reason it suggests.</summary>
    public void RaiseDied(TunnelEndReason reason) => Died?.Invoke(reason);

    private void SetState(TunnelState state)
    {
        State = state;
        StateChanged?.Invoke(state);
    }
}

/// <summary>Hands out <see cref="FakeTunnelSession"/> instances and records what the coordinator asked for.</summary>
public sealed class FakeTunnelSessionFactory : ITunnelSessionFactory
{
    /// <summary>What the guest's <c>Proxy</c> reports once the tunnel is up (the real one comes from ConnectProxyServer).</summary>
    public static readonly GuestProxyInfo Proxy = new(8080, "http://check.routebridge/");

    public List<TunnelSessionRequest> Requests { get; } = new();

    public List<FakeTunnelSession> Sessions { get; } = new();

    public FakeTunnelSession Last => Sessions[^1];

    /// <summary>Applied to every session before it is handed out (connect result, stats, diagnostics…).</summary>
    public Action<FakeTunnelSession>? Configure { get; set; }

    /// <summary>The distinct domains the host's egress claims to have seen.</summary>
    public IReadOnlyCollection<string> Domains { get; set; } = Array.Empty<string>();

    /// <summary>Set to make <see cref="Create"/> throw (a session that cannot even be prepared).</summary>
    public Exception? Throw { get; set; }

    public ITunnelSession Create(TunnelSessionRequest request)
    {
        Requests.Add(request);
        if (Throw is { } error)
        {
            throw error;
        }

        var session = new FakeTunnelSession(request.Material, request.CloseBrowserAsync)
        {
            DomainsSeen = Domains,
            Proxy = request.Material.Role == TunnelRole.Guest ? Proxy : null,
        };

        Configure?.Invoke(session);
        Sessions.Add(session);
        return session;
    }
}
