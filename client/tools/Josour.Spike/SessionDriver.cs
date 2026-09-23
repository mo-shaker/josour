using System.Diagnostics;
using System.Net;
using System.Text;
using Josour.Browser;
using Josour.Core.Browser;
using Josour.Core.Control;
using Josour.Core.Session;
using Josour.Core.Tunnel;
using Josour.Egress;
using Josour.Infrastructure.Session;
using Josour.Proxy;
using Josour.Tunnel;

namespace Josour.Spike;

/// <summary>How the session ended. <see cref="SessionCommand.ExitFor"/> translates it into an exit code.</summary>
public enum SessionOutcomeKind
{
    /// <summary>No connection authenticated within the timeout (or the preparation was refused). <c>session.connect_failed</c> was sent.</summary>
    ConnectFailed,

    /// <summary>The tunnel died of its own accord after having worked (a drop, or the other side's process dying, or the PONG timeout).</summary>
    TunnelDied,

    /// <summary>The server ended the session (<c>session.terminate</c>): the duration running out, the other side ending it, or an administrator ending it.</summary>
    Terminated,

    /// <summary><c>expires_at</c> was reached locally before <c>session.terminate</c> arrived.</summary>
    Expired,

    /// <summary>A deliberate local end (Ctrl+C, or the user pressing "end"). <c>session.end</c> was sent.</summary>
    LocalEnd,

    /// <summary>The control channel fell during the session; the server ends it with <c>*_disconnected</c>, and a tunnel with no control is worthless.</summary>
    ChannelLost,

    /// <summary>The server sent something that breaches the contract, or the session ended for another local reason (such as <c>browser_not_proxied</c>).</summary>
    ProtocolError,
}

/// <summary>The session's complete outcome as it appears in <c>tool.exit</c>.</summary>
public sealed record SessionOutcome(
    SessionOutcomeKind Kind,
    TunnelEndReason EndReason,
    TunnelStats Stats,
    IReadOnlyCollection<string> Domains,
    string? Detail);

/// <summary>
/// Everything the tool needs around <see cref="SessionCoordinator"/>: the coordinator itself owns the whole session cycle after
/// <c>session.created</c> (the same code the WPF application drives), and this class provides what belongs to the tool alone —
/// a headless tunnel factory, and wrapping the channel, the tunnel and the browser so every event comes out as one JSON line as in
/// <c>docs/spike-runbook.md</c>, and one outcome that translates into the exit code.
///
/// <para>
/// No session logic here: no cleanup order, no decision about the end reason sent to the server, and no timers. If any of that is needed,
/// its place is <see cref="SessionCoordinator"/> rather than here.
/// </para>
/// </summary>
internal sealed class SessionDriver : ITunnelSessionFactory, IAsyncDisposable
{
    private readonly Args _args;
    private readonly EventLog _log;
    private readonly string? _curlTest;
    private readonly WorkBrowserSession? _browser;
    private readonly TaskCompletionSource _ended = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object _gate = new();

    private SessionCoordinator? _coordinator;
    private TunnelSession? _tunnel;
    private Ending? _ending;
    private bool _sessionEndSent;

    public SessionDriver(Args args, EventLog log, string? curlTest, bool wantsBrowser)
    {
        _args = args;
        _log = log;
        _curlTest = curlTest;
        if (wantsBrowser)
        {
            var kind = (args.Get("browser") ?? "chrome").ToLowerInvariant() switch
            {
                "edge" => BrowserKind.Edge,
                _ => BrowserKind.Chrome,
            };
            _browser = new WorkBrowserSession(new SpikeBrowserProvider(kind, args.Get("profile") ?? BrowserCommandLine.DefaultProfileDirectory(), log));
        }
    }

    /// <summary>
    /// The work browser handed to the coordinator: the real one with <c>--browser</c>, otherwise <see cref="NoWorkBrowser"/>, which makes
    /// the coordinator skip launching and waiting for the check page instead of ending the session with <c>browser_not_proxied</c> when there is no browser at all.
    /// </summary>
    public IWorkBrowser Browser => _browser ?? (IWorkBrowser)NoWorkBrowser.Instance;

    /// <summary>The role, learned from <c>session.created</c> through the coordinator (or from the arguments before that).</summary>
    public TunnelRole Role { get; set; } = TunnelRole.Guest;

    /// <summary>Completes when the coordinator reaches <see cref="SessionPhase.Ended"/> (that is, after the cleanup order finishes).</summary>
    public Task Ended => _ended.Task;

    // ---------- The wiring ----------

    /// <summary>Wraps the control channel so it emits <c>frame.out</c> for every frame the coordinator sends (the tool's own frames are not from it).</summary>
    public IControlChannel Wrap(IControlChannel channel) => new EventControlChannel(channel, OnFrameSent, OnFrameFailed);

    public void Attach(SessionCoordinator coordinator)
    {
        _coordinator = coordinator;
        coordinator.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SessionCoordinator.Phase))
            {
                OnPhase(coordinator.Phase);
            }
        };
    }

    /// <summary>An inbound frame belonging to the session's stream: the same events the old session driver emitted over <c>frame.in</c>.</summary>
    public void OnFrameReceived(ControlMessage message)
    {
        switch (message)
        {
            case SessionCreatedMessage m:
                // From here the coordinator takes over: this event precedes its first step because this subscriber precedes it on the channel.
                _log.Emit("session.created", EventLog.Fields(
                    ("session_id", m.SessionId), ("peer_display_name", m.Peer.UserDisplayName), ("peer_device_name", m.Peer.DeviceName),
                    ("peer_public_ip", m.PeerPublicIp), ("same_public_ip", m.SamePublicIp),
                    ("expires_at", m.ExpiresAt.ToString("O")), ("allowlist_version", m.AllowlistVersion)),
                    $"session {m.SessionId} with {m.Peer.UserDisplayName}/{m.Peer.DeviceName}; expires {m.ExpiresAt:u}");
                break;

            case SessionActiveMessage m:
                _log.Emit("session.active", EventLog.Fields(("expires_at", m.ExpiresAt.ToString("O"))), $"session is active until {m.ExpiresAt:O}");
                break;

            case SessionTerminateMessage m:
                _log.Emit("session.terminate", EventLog.Fields(("reason", m.Reason)), $"the server ended the session: {m.Reason}");
                SetEnding(SessionOutcomeKind.Terminated, SessionEndReasonNames.Parse(m.Reason), m.Reason);
                break;
        }
    }

    /// <summary>The control channel falling during the session: the server ends it with <c>*_disconnected</c>, and a tunnel with no control is worthless.</summary>
    public void OnChannelState(ControlChannelState state)
    {
        if (state == ControlChannelState.Connected || _coordinator?.HasLiveSession != true) return;
        SetEnding(SessionOutcomeKind.ChannelLost, SessionEndReasonNames.LocalDisconnect(Role), state.ToString());
    }

    /// <summary>Ctrl+C: a deliberate local end with the full cleanup order, not killing the process.</summary>
    public Task RequestEndAsync()
    {
        SetEnding(SessionOutcomeKind.LocalEnd, SessionEndReasonNames.LocalEnd(Role), "local");
        return _coordinator?.DisconnectAsync() ?? Task.CompletedTask;
    }

    /// <summary>
    /// The unauthorised access attempts on the tunnel's listener, under the keys reserved in <c>docs/api.md</c>
    /// (<c>POST /api/v1/diagnostics</c>): <c>listener_unauthenticated</c>, <c>listener_port</c>
    /// and <c>unauthenticated_peers</c> (≤ 10 addresses), carried over from <c>ITunnelSession.Diagnostics</c> unchanged
    /// and unrenamed. <c>null</c> if no tunnel was created at all (so there is no listener and nothing to report).
    ///
    /// <para>It is reported even when the counter is zero: the server writes a <c>security_events</c> row only for a positive value, but
    /// the sessions with no attempts are the rate's denominator — and with no denominator "ten attempts" means nothing.</para>
    /// </summary>
    public (Guid SessionId, Dictionary<string, object?> Data)? ListenerDiagnostics()
    {
        var tunnel = Volatile.Read(ref _tunnel);
        if (tunnel is null) return null;
        var diagnostics = tunnel.Diagnostics;
        if (!diagnostics.TryGetValue("listener_unauthenticated", out var count)) return null;

        var data = new Dictionary<string, object?>(StringComparer.Ordinal) { ["listener_unauthenticated"] = count };
        foreach (var key in new[] { "listener_port", "unauthenticated_peers", "unauthenticated_peers_distinct" })
        {
            if (diagnostics.TryGetValue(key, out var value)) data[key] = value;
        }
        return (tunnel.SessionId, data);
    }

    /// <summary>The final outcome after <see cref="Ended"/>.</summary>
    public SessionOutcome Outcome()
    {
        var ending = Volatile.Read(ref _ending) ?? new Ending(SessionOutcomeKind.ProtocolError, TunnelEndReason.ProtocolError, "the session ended without a reason");
        var tunnel = Volatile.Read(ref _tunnel);
        return new SessionOutcome(
            ending.Kind,
            ending.Reason,
            tunnel?.Stats ?? new TunnelStats(0, 0, 0),
            tunnel?.DomainsSeen ?? Array.Empty<string>(),
            ending.Detail);
    }

    // ---------- The tunnel factory ----------

    /// <summary>
    /// The same composition as <c>Josour.App.Services.TunnelSessionFactory</c> (<see cref="EgressTunnelAdapter.Create"/> on
    /// the host, <see cref="ProxyTunnelAdapter.Create"/> on the guest, and the browser-close hook), plus what the tool alone adds:
    /// <c>--listen-port</c> and <c>--no-upnp</c>, and the owning-PID check when there is a real browser. With no browser, null is passed to
    /// the proxy: no browser process owns the local connections, and the check would have refused the <c>--curl-test</c> request itself.
    /// </summary>
    public ITunnelSession Create(TunnelSessionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        Role = request.Material.Role;
        IBrowserSession? proxyBrowser = request.Browser is NoWorkBrowser ? null : request.Browser;

        // Cleanup step 2: the coordinator's own hook, with a human line explaining what is happening when there really is a browser.
        Func<CancellationToken, Task> closeBrowser = request.CloseBrowserAsync;
        if (proxyBrowser is not null)
        {
            closeBrowser = async token =>
            {
                _log.Note("closing the work browser (WM_CLOSE, then the job object after 3 s)");
                await request.CloseBrowserAsync(token).ConfigureAwait(false);
            };
        }

        var options = new TunnelSessionOptions
        {
            Allowlist = request.Allowlist,
            AllowedPorts = request.AllowedPorts,
            OurPublicIp = request.OurPublicIp,
            ListenPort = _args.GetInt("listen-port", 0),
            EnableUpnp = !_args.Has("no-upnp"),
            HostEgress = EgressTunnelAdapter.Create,
            GuestProxy = context => ProxyTunnelAdapter.Create(context, proxyBrowser, proxyBrowser is null ? null : OwnerChecker()),
            CloseBrowserAsync = closeBrowser,
        };

        var tunnel = new TunnelSession(request.Material, options);
        Volatile.Write(ref _tunnel, tunnel);
        return new EventTunnelSession(tunnel, this);
    }

    private static IOwnerPidChecker OwnerChecker()
        => OperatingSystem.IsWindows() ? WindowsOwnerPidChecker.Instance : PermissiveOwnerPidChecker.Instance;

    // ---------- After connecting, on the guest's side ----------

    /// <summary>
    /// The direct proof with no browser: run once after the connection succeeds on the guest. Its errors are recorded and do not end the session.
    /// (Launching the work browser is not here: the coordinator launches it at <c>session.active</c>, as the application does.)
    /// </summary>
    private async Task AfterConnectedAsync(GuestProxyInfo proxy, CancellationToken ct)
    {
        _log.Emit("proxy.ready", EventLog.Fields(("port", proxy.Port), ("probe_url", proxy.ProbeUrl)),
            $"local proxy on 127.0.0.1:{proxy.Port} — point a browser at it, or open {proxy.ProbeUrl}");

        if (_curlTest is not null)
        {
            await CurlThroughProxyAsync(proxy.Port, _curlTest, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// An HTTP GET request through the local proxy itself. A site that echoes the requester's address (such as <c>https://api.ipify.org</c>) makes the body
    /// itself the proof that the egress happened from the host's address.
    /// </summary>
    private async Task CurlThroughProxyAsync(int proxyPort, string url, CancellationToken ct)
    {
        var clock = Stopwatch.StartNew();
        try
        {
            using var handler = new HttpClientHandler
            {
                Proxy = new WebProxy($"http://127.0.0.1:{proxyPort}", BypassOnLocal: false),
                UseProxy = true,
                AllowAutoRedirect = false,
            };
            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Josour.Spike/session");

            using var response = await http.GetAsync(url, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
            var bytes = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            var prefix = Encoding.UTF8.GetString(bytes, 0, Math.Min(bytes.Length, 200)).ReplaceLineEndings(" ").Trim();
            _log.Emit("curl.result", EventLog.Fields(
                ("url", url), ("status", (int)response.StatusCode), ("elapsed_ms", clock.ElapsedMilliseconds),
                ("bytes", bytes.Length), ("body_prefix", prefix)),
                $"GET {url} through the proxy → {(int)response.StatusCode} in {clock.ElapsedMilliseconds} ms, {bytes.Length} B: {prefix}");
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _log.Emit("curl.failed", EventLog.Fields(("url", url), ("elapsed_ms", clock.ElapsedMilliseconds), ("error", $"{e.GetType().Name}: {e.Message}")),
                $"GET {url} through the proxy failed after {clock.ElapsedMilliseconds} ms: {e.Message}");
        }
    }

    // ---------- The events ----------

    private void OnPhase(SessionPhase phase)
    {
        switch (phase)
        {
            case SessionPhase.Ending:
            {
                var ending = Fallback();
                _log.Emit("session.ending", EventLog.Fields(
                    ("kind", ending.Kind.ToString()), ("reason", ending.Reason.ToString()), ("detail", ending.Detail)),
                    $"cleaning up ({ending.Reason})");
                break;
            }

            case SessionPhase.Ended:
            {
                var outcome = Outcome();
                _log.Emit("session.ended", EventLog.Fields(
                    ("kind", outcome.Kind.ToString()), ("reason", outcome.EndReason.ToString()), ("detail", outcome.Detail),
                    ("bytes_up", outcome.Stats.BytesUp), ("bytes_down", outcome.Stats.BytesDown),
                    ("domains", outcome.Domains.ToList()), ("server_already_ended", !Volatile.Read(ref _sessionEndSent))),
                    $"session ended: {outcome.Kind}");
                _ended.TrySetResult();
                break;
            }
        }
    }

    private void OnFrameSent(ControlMessage message)
    {
        _log.Emit("frame.out", EventLog.Fields(("type", message.Type)), $"→ {message.Type}");
        switch (message)
        {
            case SessionStatsMessage m:
                var open = Volatile.Read(ref _tunnel)?.Stats.OpenStreams ?? 0;
                _log.Emit("session.stats", EventLog.Fields(("bytes_up", m.BytesUp), ("bytes_down", m.BytesDown), ("open_streams", open)),
                    $"stats: {m.BytesUp} B up / {m.BytesDown} B down, {open} open stream(s)");
                break;

            case SessionConnectFailedMessage m:
                var reason = m.Diagnostics.TryGetValue("failure_reason", out var value) ? value : null;
                _log.Emit("tunnel.connect_failed", ConnectFailedFields(m), $"connect FAILED: {Text(reason)}");
                SetEnding(SessionOutcomeKind.ConnectFailed, TunnelEndReason.ConnectFailed, reason?.ToString());
                break;

            case SessionEndMessage:
                Volatile.Write(ref _sessionEndSent, true);
                break;
        }
    }

    private void OnFrameFailed(ControlMessage message, Exception error)
        => _log.Emit("frame.out_failed", EventLog.Fields(("type", message.Type), ("error", $"{error.GetType().Name}: {error.Message}")));

    /// <summary>The connection's diagnostics as they were: the contents of <c>connect</c> flattened, with <c>reason</c> and <c>role</c>.</summary>
    private Dictionary<string, object?> ConnectFailedFields(SessionConnectFailedMessage message)
    {
        var fields = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (message.Diagnostics.TryGetValue("connect", out var connect) && connect is IReadOnlyDictionary<string, object?> map)
        {
            foreach (var (key, value) in map) fields[key] = value;
        }

        fields["reason"] = message.Diagnostics.TryGetValue("failure_reason", out var reason) ? reason : "unknown";
        fields["role"] = Role == TunnelRole.Host ? "host" : "guest";
        return fields;
    }

    /// <summary>The first ending condition is the real one; a later one is ignored (as it was in the old driver).</summary>
    private void SetEnding(SessionOutcomeKind kind, TunnelEndReason reason, string? detail)
    {
        lock (_gate)
        {
            _ending ??= new Ending(kind, reason, detail);
        }
    }

    /// <summary>No explicit source (the duration running out, <c>browser_not_proxied</c>, a protocol error): we read the coordinator's own verdict.</summary>
    private Ending Fallback()
    {
        var wire = _coordinator?.LastEndReason;
        SessionEndReasonNames.TryParse(wire, out var reason);
        var kind = reason switch
        {
            TunnelEndReason.Expired => SessionOutcomeKind.Expired,
            TunnelEndReason.GuestEnded or TunnelEndReason.HostEnded => SessionOutcomeKind.LocalEnd,
            TunnelEndReason.ConnectFailed => SessionOutcomeKind.ConnectFailed,
            TunnelEndReason.GuestDisconnected or TunnelEndReason.HostDisconnected => SessionOutcomeKind.TunnelDied,
            _ => SessionOutcomeKind.ProtocolError,
        };

        lock (_gate)
        {
            return _ending ??= new Ending(kind, reason, wire);
        }
    }

    private static string Text(object? value) => value?.ToString() ?? "null";

    public async ValueTask DisposeAsync()
    {
        if (_browser is not null) await _browser.DisposeAsync().ConfigureAwait(false);
    }

    private sealed record Ending(SessionOutcomeKind Kind, TunnelEndReason Reason, string? Detail);

    // ---------- The wrapping ----------

    /// <summary>
    /// The control channel as the coordinator sees it: everything passes through as it is, and every outbound frame becomes a <c>frame.out</c>. It does not own the inner
    /// channel and does not close it (<see cref="SessionStack"/> owns it).
    /// </summary>
    private sealed class EventControlChannel : IControlChannel
    {
        private readonly IControlChannel _inner;
        private readonly Action<ControlMessage> _sent;
        private readonly Action<ControlMessage, Exception> _failed;

        public EventControlChannel(IControlChannel inner, Action<ControlMessage> sent, Action<ControlMessage, Exception> failed)
        {
            _inner = inner;
            _sent = sent;
            _failed = failed;
            _inner.MessageReceived += Forward;
            _inner.StateChanged += ForwardState;
            _inner.Closed += ForwardClosed;
        }

        public ControlChannelState State => _inner.State;

        public event Action<ControlChannelState>? StateChanged;

        public event Action<ControlMessage>? MessageReceived;

        public event Action<ControlChannelClosed>? Closed;

        public Task<HelloAckMessage> ConnectAsync(string accessToken, Guid deviceId, Dictionary<string, object?>? diagnostics, CancellationToken ct)
            => _inner.ConnectAsync(accessToken, deviceId, diagnostics, ct);

        public async Task SendAsync(ControlMessage message, CancellationToken ct)
        {
            try
            {
                await _inner.SendAsync(message, ct).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                _failed(message, e);
                throw;
            }

            _sent(message);
        }

        public async Task<ControlMessage> RequestAsync(ControlMessage message, TimeSpan timeout, CancellationToken ct)
        {
            ControlMessage reply;
            try
            {
                reply = await _inner.RequestAsync(message, timeout, ct).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                _failed(message, e);
                throw;
            }

            _sent(message);
            return reply;
        }

        public ValueTask DisposeAsync()
        {
            _inner.MessageReceived -= Forward;
            _inner.StateChanged -= ForwardState;
            _inner.Closed -= ForwardClosed;
            return ValueTask.CompletedTask;
        }

        private void Forward(ControlMessage message) => MessageReceived?.Invoke(message);

        private void ForwardState(ControlChannelState state) => StateChanged?.Invoke(state);

        private void ForwardClosed(ControlChannelClosed closed) => Closed?.Invoke(closed);
    }

    /// <summary>
    /// The tunnel as the coordinator sees it: everything passes through to the real <see cref="TunnelSession"/>, and every step becomes an event in the JSON stream.
    /// And this is also where the guest's after-connect work lives (the proxy's port and <c>--curl-test</c>), exactly where it was before.
    /// </summary>
    private sealed class EventTunnelSession : ITunnelSession
    {
        private readonly TunnelSession _inner;
        private readonly SessionDriver _driver;

        public EventTunnelSession(TunnelSession inner, SessionDriver driver)
        {
            _inner = inner;
            _driver = driver;
            _inner.StateChanged += OnState;
            _inner.ProbeSeen += OnProbe;
            _inner.Died += OnDied;
        }

        public Guid SessionId => _inner.SessionId;

        public TunnelRole Role => _inner.Role;

        public TunnelState State => _inner.State;

        public TunnelStats Stats => _inner.Stats;

        public IReadOnlyCollection<string> DomainsSeen => _inner.DomainsSeen;

        public GuestProxyInfo? Proxy => _inner.Proxy;
        public IReadOnlyDictionary<string, long>? ProxyCounters => _inner.ProxyCounters;

        public IReadOnlyDictionary<string, object?> Diagnostics => _inner.Diagnostics;

        public event Action<TunnelState>? StateChanged;

        public event Action? ProbeSeen;

        public event Action<TunnelEndReason>? Died;

        public async Task<LocalEndpointInfo> PrepareAsync(CancellationToken ct)
        {
            LocalEndpointInfo local;
            try
            {
                local = await _inner.PrepareAsync(ct).ConfigureAwait(false);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                _driver._log.Emit("tunnel.prepare_failed", EventLog.Fields(("error", $"{e.GetType().Name}: {e.Message}")));
                throw;
            }

            _driver._log.Emit("tunnel.prepared", EventLog.Fields(
                ("listen_port", _inner.ListenPort),
                ("cert_fp_sha256", local.CertFingerprintSha256Hex),
                ("candidates", Describe(local.Candidates))),
                $"listening on port {_inner.ListenPort}; sending session.endpoint");
            return local;
        }

        public async Task<TunnelConnectResult> ConnectAsync(PeerEndpointInfo peer, TimeSpan timeout, CancellationToken ct)
        {
            _driver._log.Emit("tunnel.peer_endpoint", EventLog.Fields(
                ("cert_fp_sha256", peer.CertFingerprintSha256Hex), ("candidates", Describe(peer.Candidates))),
                "peer endpoint received; dialling every candidate in parallel");

            var result = await _inner.ConnectAsync(peer, timeout, ct).ConfigureAwait(false);
            if (!result.Connected)
            {
                return result; // the coordinator answers with session.connect_failed, which raises tunnel.connect_failed
            }

            // The window is derived from connect_ms (docs/protocol.md section 5): it is printed in a field run because the band
            // explains the single-download ceiling and the concurrent stream limit in that same run.
            _driver._log.Emit("tunnel.connected", EventLog.Fields(
                ("winner_type", result.WinnerType is null ? null : CandidateTypeNames.ToWire(result.WinnerType.Value)),
                ("connect_ms", result.ConnectMs), ("tls_version", result.TlsVersion),
                ("mux_window", _inner.Diagnostics.TryGetValue("mux_window", out var window) ? window : null),
                ("mux_max_streams", _inner.Diagnostics.TryGetValue("mux_max_streams", out var streams) ? streams : null),
                ("proxy_port", _inner.Proxy?.Port), ("probe_url", _inner.Proxy?.ProbeUrl)),
                $"CONNECTED via {Text(result.WinnerType is null ? null : CandidateTypeNames.ToWire(result.WinnerType.Value))} in {result.ConnectMs} ms over TLS {Text(result.TlsVersion)}");

            if (_inner.Proxy is { } proxy)
            {
                try
                {
                    await _driver.AfterConnectedAsync(proxy, ct).ConfigureAwait(false);
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    _driver._log.Emit("after_connected_failed", EventLog.Fields(("error", $"{e.GetType().Name}: {e.Message}")));
                }
            }

            return result;
        }

        public Task EndAsync(TunnelEndReason reason, CancellationToken ct) => _inner.EndAsync(reason, ct);

        public ValueTask DisposeAsync()
        {
            _inner.StateChanged -= OnState;
            _inner.ProbeSeen -= OnProbe;
            _inner.Died -= OnDied;
            return _inner.DisposeAsync();
        }

        private static List<Dictionary<string, object?>> Describe(IReadOnlyList<CandidateEndpoint> candidates)
            => candidates.Select(c => new Dictionary<string, object?>
            {
                ["type"] = CandidateTypeNames.ToWire(c.Type),
                ["ip"] = c.Ip,
                ["port"] = c.Port,
            }).ToList();

        private void OnState(TunnelState state)
        {
            _driver._log.Emit("tunnel.state", EventLog.Fields(("state", state.ToString())), $"tunnel → {state}");
            StateChanged?.Invoke(state);
        }

        private void OnProbe() => ProbeSeen?.Invoke();

        private void OnDied(TunnelEndReason reason)
        {
            _driver._log.Emit("tunnel.died", EventLog.Fields(("suggested_reason", reason.ToString())), $"the tunnel died: {reason}");
            _driver.SetEnding(SessionOutcomeKind.TunnelDied, reason, reason.ToString());
            Died?.Invoke(reason);
        }
    }

    /// <summary>The tool's real work browser: one kind (<c>--browser</c>) and one profile (<c>--profile</c>).</summary>
    private sealed class SpikeBrowserProvider : IWorkBrowserProvider
    {
        private readonly BrowserKind _kind;
        private readonly EventLog _log;

        public SpikeBrowserProvider(BrowserKind kind, string profileDirectory, EventLog log)
        {
            _kind = kind;
            _log = log;
            ProfileDirectory = profileDirectory;
        }

        public string ProfileDirectory { get; }

        public IReadOnlyList<BrowserKind> Preference() => new[] { _kind };

        public IBrowserSession Create(BrowserKind kind) => new EventBrowserSession(BrowserSessions.ForCurrentPlatform(), kind, _log);
    }

    /// <summary>
    /// The platform's own <see cref="IBrowserSession"/>, emitting a <c>browser.launch</c> event with every launch's
    /// result. It used to construct <see cref="BrowserLauncher"/> by name, which is the Windows implementation, so
    /// the tool that verifies the stack could not launch a browser on macOS at all.
    /// </summary>
    private sealed class EventBrowserSession : IBrowserSession
    {
        private readonly IBrowserSession _inner;
        private readonly BrowserKind _kind;
        private readonly EventLog _log;

        public EventBrowserSession(IBrowserSession inner, BrowserKind kind, EventLog log)
        {
            _inner = inner;
            _kind = kind;
            _log = log;
        }

        public bool IsRunning => _inner.IsRunning;

        public bool OwnsProcess(int pid) => _inner.OwnsProcess(pid);

        public async Task<BrowserLaunchResult> LaunchAsync(BrowserLaunchOptions options, CancellationToken ct)
        {
            var result = await _inner.LaunchAsync(options, ct).ConfigureAwait(false);
            _log.Emit("browser.launch", EventLog.Fields(
                ("browser", _kind.ToString().ToLowerInvariant()), ("success", result.Success), ("failure", result.Failure?.ToString()),
                ("detail", result.Detail), ("handoff", (_inner as BrowserLauncher)?.InstanceHandoff), ("pid", ProcessId())),
                result.Success ? $"launched {_kind} on the check page" : $"could not launch {_kind}: {result.Failure} — {result.Detail}");
            return result;
        }

        /// <summary>
        /// Instance handoff is a Windows notion — a second copy of Chrome handing the URL to the first — and is left
        /// null elsewhere rather than invented. A process id is not: every platform has one, so it is read from
        /// whichever session ran.
        /// </summary>
        private int? ProcessId() => _inner switch
        {
            BrowserLauncher windows => windows.ProcessId,
            MacBrowserSession mac => mac.ProcessId,
            _ => null,
        };

        public Task CloseAsync(TimeSpan graceful, CancellationToken ct) => _inner.CloseAsync(graceful, ct);

        public ValueTask DisposeAsync() => _inner.DisposeAsync();
    }
}
