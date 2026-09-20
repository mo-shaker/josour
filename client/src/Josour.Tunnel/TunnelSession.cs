using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using Josour.Core.Tunnel;
using Josour.Tunnel.Candidates;
using Josour.Tunnel.Certificates;
using Josour.Tunnel.Mux;
using Josour.Tunnel.Transport;

namespace Josour.Tunnel;

/// <summary>
/// The <see cref="ITunnelSession"/> implementation for both roles: it gathers the existing pieces (the certificate, the listener, the candidate gatherer,
/// the symmetric connector, the mux, the egress policy on the host, the proxy on the guest) into one object that owns the lifecycle and the cleanup order.
///
/// The lifecycle (docs/protocol.md sections 2 and 7):
///   PrepareAsync -> Listening (a certificate + a listener + candidates; what is sent in session.endpoint)
///   ConnectAsync -> Connecting -> Authenticating -> Connected (the mux + the role-specific end)
///   the tunnel dying -> a Died event with a suggested reason (never raised for a clean close)
///   EndAsync     -> Ended (the six-step cleanup order, safe from any state and safe to repeat)
///
/// What stays with the application (track C): launching and closing the browser (through <see cref="TunnelSessionOptions.CloseBrowserAsync"/>),
/// and every server message (session.endpoint / connected / connect_failed / stats / end).
///
/// Ownership of the secrets: the session owns <c>material.Secret</c> and wipes it in step 5 of the cleanup.
/// </summary>
public sealed class TunnelSession : ITunnelSession
{
    private readonly SessionMaterial _material;
    private readonly TunnelSessionOptions _options;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _endGate = new();
    private readonly object _diagnosticsGate = new();
    private readonly Dictionary<string, object?> _diagnostics = new(StringComparer.Ordinal);

    private SessionCertificate? _certificate;
    private TunnelListener? _listener;
    private ICandidateSource? _candidateSource;
    private LocalEndpointInfo? _local;
    private NerdbankMux? _mux;
    private ITunnelEgress? _egress;
    private ITunnelProxy? _proxy;
    private Task? _deathWatch;
    private Task? _endTask;
    private TunnelStats _finalStats = new(0, 0, 0);
    private IReadOnlyCollection<string> _finalDomains = Array.Empty<string>();
    private int _state = (int)TunnelState.Idle;
    private int _connectStarted;
    private int _ending;
    private int _diedRaised;
    private int _probeRaised;

    public TunnelSession(SessionMaterial material, TunnelSessionOptions? options = null)
    {
        _material = material ?? throw new ArgumentNullException(nameof(material));
        if (material.Secret is null || material.Secret.Length != 32)
            throw new ArgumentException("session secret must be exactly 32 bytes", nameof(material));
        _options = options ?? new TunnelSessionOptions();
        Note("role", material.Role == TunnelRole.Host ? "host" : "guest");
        Note("state", TunnelState.Idle.ToString());
        Note("transport", _options.Transport.Name);
    }

    public Guid SessionId => _material.SessionId;
    public TunnelRole Role => _material.Role;
    public TunnelState State => (TunnelState)Volatile.Read(ref _state);

    public event Action<TunnelState>? StateChanged;
    public event Action? ProbeSeen;
    public event Action<TunnelEndReason>? Died;

    public GuestProxyInfo? Proxy { get; private set; }

    public IReadOnlyDictionary<string, long>? ProxyCounters => _proxy?.Counters;

    /// <summary>When the check page first arrived (for late subscribers to <see cref="ProbeSeen"/>).</summary>
    public DateTimeOffset? ProbeSeenAt { get; private set; }

    /// <summary>The candidate-gathering diagnostics for the relay decision gate (ADR-0003). null before PrepareAsync.</summary>
    public GatherDiagnostics? GatherDiagnostics { get; private set; }

    /// <summary>The listening port sent in host.available.listen_port. Zero before PrepareAsync or after the cleanup.</summary>
    public int ListenPort => _listener?.Port ?? 0;

    /// <summary>This session's certificate fingerprint (the same as in LocalEndpointInfo). null before PrepareAsync.</summary>
    public string? CertificateFingerprintHex => _local?.CertFingerprintSha256Hex;

    /// <summary>Has the certificate been deleted (cleanup step 5)? true before it is created, too.</summary>
    public bool CertificateDisposed => _certificate?.IsDisposed ?? true;

    /// <summary>Is the mux closed (cleanup step 3)? true before connecting, too.</summary>
    public bool MuxClosed => _mux?.IsClosed ?? true;

    public TunnelStats Stats => State == TunnelState.Ended ? _finalStats : SnapshotStats();

    public IReadOnlyCollection<string> DomainsSeen
        => State == TunnelState.Ended ? _finalDomains : _egress?.Domains ?? Array.Empty<string>();

    public IReadOnlyDictionary<string, object?> Diagnostics
    {
        get { lock (_diagnosticsGate) return new Dictionary<string, object?>(_diagnostics, StringComparer.Ordinal); }
    }

    // ---------- 1. PrepareAsync ----------

    /// <summary>
    /// docs/protocol.md section 2 step 1: the session certificate + a TCP listener + the UPnP mapping + gathering the candidates.
    /// Idempotent (it returns the same result). It moves the state to Listening.
    /// </summary>
    public async Task<LocalEndpointInfo> PrepareAsync(CancellationToken ct)
    {
        ThrowIfEnded();
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_local is { } prepared) return prepared;
            ThrowIfEnded();

            var certificate = SessionCertificate.Create(_material.ExpiresAt);
            TunnelListener? listener = null;
            ICandidateSource? source = null;
            try
            {
                listener = new TunnelListener(_options.ListenPort, _options.BindAddress);
                source = _options.CandidateSource?.Invoke() ?? new CandidateGatherer(_options.UpnpDiscoveryTimeout);
                var gatherOptions = new GatherOptions(listener.Port, _options.OurPublicIp, _material.SamePublicIp, MappingLifetime(), _options.EnableUpnp);
                var gather = await source.GatherAsync(gatherOptions, ct).ConfigureAwait(false);

                _certificate = certificate;
                _listener = listener;
                _candidateSource = source;
                GatherDiagnostics = gather.Diagnostics;
                _local = new LocalEndpointInfo(certificate.FingerprintHex, gather.Candidates);

                Note("listener_port", listener.Port);
                Note("gather", gather.Diagnostics.ToDictionary());
                Note("local_candidates", DescribeCandidates(gather.Candidates));
                SetState(TunnelState.Listening);
                return _local;
            }
            catch
            {
                certificate.Dispose();
                if (source is not null) await SafeDisposeAsync(source).ConfigureAwait(false);
                if (listener is not null) await SafeDisposeAsync(listener).ConfigureAwait(false);
                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    // ---------- 2. ConnectAsync ----------

    /// <summary>
    /// docs/protocol.md section 2 steps 3-6 through <see cref="SymmetricConnector"/>, then the mux and the role-specific end:
    /// the host wires the egress policy to an acceptor, and the guest starts the local proxy and exposes its port and the check page's URL.
    /// On failure the state stays Connecting (the listener was closed) and it returns the result with the failure's reason, with each candidate's diagnostics in <see cref="Diagnostics"/>.
    /// </summary>
    public async Task<TunnelConnectResult> ConnectAsync(PeerEndpointInfo peer, TimeSpan timeout, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(peer);
        ThrowIfEnded();
        if (_local is null || _listener is null || _certificate is null)
            throw new InvalidOperationException("PrepareAsync must complete before ConnectAsync");
        ValidateRoleFactories();
        if (Interlocked.Exchange(ref _connectStarted, 1) != 0)
            throw new InvalidOperationException("ConnectAsync may be called only once");

        SetState(TunnelState.Connecting);
        var connector = new SymmetricConnector(_material, _certificate, _listener, _options.Transport, _local.Candidates, BuildRelayLeg(timeout));
        void OnStage(string stage)
        {
            if (stage == "auth" && State == TunnelState.Connecting) SetState(TunnelState.Authenticating);
        }

        SymmetricConnectOutcome outcome;
        connector.StageReached += OnStage;
        try
        {
            outcome = await connector.ConnectAsync(peer, timeout, ct).ConfigureAwait(false);
        }
        finally
        {
            connector.StageReached -= OnStage;
        }

        Note("connect", outcome.Diagnostics);
        NoteUnauthenticatedProbes(); // the listener is closed now, so the count is final even if the connection failed
        if (!outcome.Result.Connected || outcome.Connection is null) return outcome.Result;

        await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            if (_endTask is not null)
            {
                // The session ended while the connection was under way: we do not build a tunnel over what will close at once.
                await SafeDisposeAsync(outcome.Connection).ConfigureAwait(false);
                return new TunnelConnectResult(false, null, outcome.Result.ConnectMs, outcome.Result.TlsVersion, "session_ended");
            }

            // docs/protocol.md section 5: the window is derived from the RTT measured at connect time, and this is the first place that knows it
            // (SymmetricConnector has just returned). Each side derives from its own measurement — the receiving window is the receiver's property
            // and is announced per channel in Nerdbank protocol 3, so no agreement on the wire is needed (see MuxWindow).
            var muxOptions = _options.Mux ?? new MuxOptions();
            var window = muxOptions.Resolve(MuxWindow.ForRoundTrip(TimeSpan.FromMilliseconds(outcome.Result.ConnectMs)));
            Note("mux_window", window.ReceiveWindow);
            Note("mux_max_streams", window.MaxConcurrentStreams);
            Note("mux_window_reason", window.Reason); // why this band, and the fallback's reason when the measurement is corrupt
            var mux = NerdbankMux.Create(outcome.Connection.Stream, _material.Role, muxOptions, window);
            try
            {
                if (_material.Role == TunnelRole.Host)
                {
                    var egress = _options.HostEgress!(new TunnelEgressContext(
                        _options.Allowlist, _options.AllowedPorts, _options.Resolver, BlockedLocalAddresses(), mux, window.MaxConcurrentStreams));
                    egress.Attach(mux);
                    _egress = egress;
                }
                else if (_options.StartGuestProxy)
                {
                    var proxy = _options.GuestProxy!(new TunnelProxyContext(
                        _options.Allowlist, _options.AllowedPorts, _options.Resolver, _material.PeerPublicIp, mux));
                    proxy.ProbeSeen += OnProbeSeen;
                    proxy.Start();
                    _proxy = proxy;
                    Proxy = new GuestProxyInfo(proxy.Port, proxy.ProbeUrl);
                    Note("proxy_port", proxy.Port);
                    Note("probe_url", proxy.ProbeUrl);
                }
            }
            catch
            {
                await SafeDisposeAsync(mux).ConfigureAwait(false);
                throw;
            }

            _mux = mux;
            _deathWatch = WatchAsync(mux);
            Note("winner_type", outcome.Result.WinnerType is null ? null : CandidateTypeNames.ToWire(outcome.Result.WinnerType.Value));
            Note("connect_ms", outcome.Result.ConnectMs);
            Note("tls_version", outcome.Result.TlsVersion);
            SetState(TunnelState.Connected);
            return outcome.Result;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Builds the relay path from <c>session.created.relay</c>, or null if no relay is configured in the deployment.
    /// <para>
    /// The handshake timeout here is the whole connect window rather than the default five seconds: the relay's reply is
    /// <c>paired</c>, and it does not arrive until <b>the peer arrives too</b> — which may be as late as the window allows. Five seconds would
    /// have dropped every session whose two sides do not start in the same instant, which is the normal case rather than the exception.
    /// </para>
    /// </summary>
    private RelayLeg? BuildRelayLeg(TimeSpan connectWindow)
    {
        if (_options.Relay is not { } relay) return null;
        var transport = new RelayTransport(new RelayTransportOptions
        {
            SessionId = _material.SessionId,
            Role = _material.Role,
            TokenProvider = _ => ValueTask.FromResult(relay.Token),
            HandshakeTimeout = connectWindow,
            // RELAY_HOST is a hostname by design, and DirectTransport deliberately refuses anything but address literals
            // (the candidates come from the peer; this address comes from our own server). See ResolvingTransport.
            Inner = new ResolvingTransport(),
        });
        Note("relay_endpoint", $"{relay.Address}:{relay.Port}"); // the token is not logged
        return new RelayLeg(transport, new CandidateEndpoint(CandidateType.Relay, relay.Address, relay.Port));
    }

    // ---------- 3. Liveness and death ----------

    /// <summary>
    /// <c>Completion</c> failing = a dead tunnel (EOF with no GOAWAY, or the PONG timeout, after week two's two fixes); it completing with no error
    /// is a clean close that is not reported. The event is raised once, and is not raised during EndAsync.
    /// </summary>
    private async Task WatchAsync(NerdbankMux mux)
    {
        try
        {
            await mux.Completion.ConfigureAwait(false);
            Note("mux_completion", "clean");
        }
        catch (Exception e)
        {
            Note("mux_completion", "faulted");
            Note("death", Describe(e));
            RaiseDied(SuggestDeathReason(mux));
        }
    }

    private TunnelEndReason SuggestDeathReason(NerdbankMux mux) => mux.RemoteGoAway switch
    {
        GoAwayReason.Expired => TunnelEndReason.Expired,
        GoAwayReason.ProtocolError => TunnelEndReason.ProtocolError,
        // No GOAWAY: the other side vanished. The server names it after whoever vanished (docs/ws-protocol.md section 5).
        _ => _material.Role == TunnelRole.Host ? TunnelEndReason.GuestDisconnected : TunnelEndReason.HostDisconnected,
    };

    private void RaiseDied(TunnelEndReason reason)
    {
        if (Volatile.Read(ref _ending) != 0) return;
        if (Interlocked.Exchange(ref _diedRaised, 1) != 0) return;
        Note("died_reason", reason.ToString());
        try { Died?.Invoke(reason); } catch { /* the listener is responsible for its own errors */ }
    }

    private void OnProbeSeen()
    {
        if (Interlocked.Exchange(ref _probeRaised, 1) != 0) return;
        ProbeSeenAt = DateTimeOffset.UtcNow;
        Note("probe_seen_at", ProbeSeenAt.Value.ToString("O"));
        try { ProbeSeen?.Invoke(); } catch { /* the listener is responsible for its own errors */ }
    }

    // ---------- 4. EndAsync ----------

    /// <summary>
    /// The cleanup order in docs/protocol.md section 7, literally. Safe from any state (even Idle) and safe to repeat: a second call
    /// waits on the first one itself. It never throws; each step's errors are recorded in <see cref="Diagnostics"/> and do not stop what follows.
    /// </summary>
    public Task EndAsync(TunnelEndReason reason, CancellationToken ct)
    {
        lock (_endGate)
        {
            if (_endTask is null)
            {
                Volatile.Write(ref _ending, 1); // at once: any drop from now on is expected and is not raised as a death
                // Outside the lock, so no hook (the browser, StateChanged) runs while it is held.
                _endTask = Task.Run(() => EndCoreAsync(reason, ct));
            }
            return _endTask;
        }
    }

    private async Task EndCoreAsync(TunnelEndReason reason, CancellationToken ct)
    {
        Volatile.Write(ref _ending, 1); // from here on every drop is expected: no Died event
        Note("end_reason", reason.ToString());
        // We wait for any preparation or wiring under way so we do not tear down a piece still being built (ConnectAsync checks _endTask under the same gate).
        await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            await CleanupAsync(reason, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }

        if (_deathWatch is { } watch)
        {
            try { await watch.ConfigureAwait(false); } catch { /* the watcher swallows its own errors */ }
        }
    }

    private async Task CleanupAsync(TunnelEndReason reason, CancellationToken ct)
    {
        // 1) The proxy stops accepting new connections.
        Try("proxy_stop_accepting", () => _proxy?.StopAccepting());

        // 2) Closing the browser: the application's own, carried out here through the hook so it stays in its place in the order.
        if (_options.CloseBrowserAsync is { } closeBrowser)
            await TryAsync("browser_close", () => closeBrowser(ct)).ConfigureAwait(false);

        // 3) GOAWAY(session_end) and closing the tunnel, then stopping the proxy entirely (its connections have no destination once the tunnel is closed).
        if (_mux is { } mux)
        {
            await TryAsync("mux_goaway", () => mux.CloseAsync(GoAwayFor(reason))).ConfigureAwait(false);
            await TryAsync("mux_dispose", () => mux.DisposeAsync().AsTask()).ConfigureAwait(false);
        }
        if (_proxy is { } proxy)
        {
            proxy.ProbeSeen -= OnProbeSeen;
            await TryAsync("proxy_stop", proxy.StopAsync).ConfigureAwait(false);
            await TryAsync("proxy_dispose", () => proxy.DisposeAsync().AsTask()).ConfigureAwait(false);
        }

        // The final statistics are captured before the egress counters are disposed.
        _finalStats = SnapshotStats() with { OpenStreams = 0 };
        _finalDomains = _egress?.Domains ?? Array.Empty<string>();
        if (_egress is { } egress) await TryAsync("egress_dispose", () => egress.DisposeAsync().AsTask()).ConfigureAwait(false);

        // 4) Closing the listener and removing the UPnP mapping.
        NoteUnauthenticatedProbes(); // the last chance: a session that ended before ConnectAsync still had a listener that ran and saw what it saw
        if (_listener is { } listener) await TryAsync("listener_dispose", () => listener.DisposeAsync().AsTask()).ConfigureAwait(false);
        if (_candidateSource is { } source)
        {
            if (source.HasMapping)
            {
                var removed = false;
                await TryAsync("upnp_remove", async () => removed = await source.RemoveMappingAsync(ct).ConfigureAwait(false)).ConfigureAwait(false);
                Note("upnp_mapping_removed", removed);
            }
            await TryAsync("candidate_source_dispose", () => source.DisposeAsync().AsTask()).ConfigureAwait(false);
        }

        // 5) Disposing the certificate and wiping the secret.
        Try("certificate_dispose", () => _certificate?.Dispose());
        CryptographicOperations.ZeroMemory(_material.Secret);

        // 6) The statistics and the domains are ready for session.end (the application sends it).
        Note("final_bytes_up", _finalStats.BytesUp);
        Note("final_bytes_down", _finalStats.BytesDown);
        Note("final_domains", _finalDomains.Count);
        SetState(TunnelState.Ended);
    }

    public async ValueTask DisposeAsync()
        => await EndAsync(_material.Role == TunnelRole.Host ? TunnelEndReason.HostEnded : TunnelEndReason.GuestEnded, CancellationToken.None).ConfigureAwait(false);

    // ---------- helpers ----------

    private static GoAwayReason GoAwayFor(TunnelEndReason reason) => reason switch
    {
        TunnelEndReason.Expired => GoAwayReason.Expired,
        TunnelEndReason.ProtocolError => GoAwayReason.ProtocolError,
        _ => GoAwayReason.SessionEnd,
    };

    private TunnelStats SnapshotStats()
    {
        var muxStats = _mux?.Stats ?? default;
        // The host reports the sites' payload (what the server wants in session.stats); the guest has no counter of its own, so it reports the transport's bytes.
        if (_material.Role == TunnelRole.Host && _egress is { } egress)
            return new TunnelStats(egress.BytesUp, egress.BytesDown, muxStats.OpenStreams);
        return new TunnelStats(muxStats.BytesUp, muxStats.BytesDown, muxStats.OpenStreams);
    }

    private IReadOnlyList<IPAddress> BlockedLocalAddresses()
    {
        var list = new List<IPAddress>(_options.LocalAddresses ?? LocalNetwork.GetPolicyLocalAddresses());
        if (IPAddress.TryParse(_options.OurPublicIp, out var ours)) list.Add(ours);
        return list;
    }

    private TimeSpan MappingLifetime()
    {
        var remaining = _material.ExpiresAt - DateTimeOffset.UtcNow;
        if (remaining < TimeSpan.Zero) remaining = TimeSpan.Zero;
        return remaining + _options.MappingLifetimeSlack;
    }

    private void ValidateRoleFactories()
    {
        if (_material.Role == TunnelRole.Host && _options.HostEgress is null)
            throw new InvalidOperationException("TunnelSessionOptions.HostEgress is required for the host role (use Josour.Egress.EgressTunnelAdapter.Create)");
        if (_material.Role == TunnelRole.Guest && _options.StartGuestProxy && _options.GuestProxy is null)
            throw new InvalidOperationException("TunnelSessionOptions.GuestProxy is required for the guest role (use Josour.Proxy.ProxyTunnelAdapter.Create, or set StartGuestProxy = false)");
    }

    private void ThrowIfEnded()
    {
        if (_endTask is not null || State == TunnelState.Ended)
            throw new InvalidOperationException("tunnel session has ended");
    }

    private void SetState(TunnelState next)
    {
        var previous = (TunnelState)Interlocked.Exchange(ref _state, (int)next);
        if (previous == next) return;
        Note("state", next.ToString());
        try { StateChanged?.Invoke(next); } catch { /* the listener is responsible for its own errors */ }
    }

    /// <summary>
    /// Unauthorised access attempts on the tunnel's listener, under the keys reserved in <c>docs/api.md</c> (<c>POST /diagnostics</c>):
    /// <c>listener_unauthenticated</c> (a count), <c>listener_port</c> (written in PrepareAsync) and <c>unauthenticated_peers</c>
    /// (IP addresses, capped at 10). Track C reads them from <see cref="Diagnostics"/> as they are and puts them in <c>data</c> unchanged.
    /// The key <c>unauthenticated_peers_distinct</c> is an optional addition: how many distinct addresses were actually seen before the cap of ten.
    /// No payloads, no source ports and no timestamps — a counter and addresses only.
    /// </summary>
    private void NoteUnauthenticatedProbes()
    {
        if (_listener is not { } listener) return;
        var probes = listener.Probes;
        Note("listener_unauthenticated", probes.Count);
        Note("unauthenticated_peers", probes.Peers.ToList());
        Note("unauthenticated_peers_distinct", probes.DistinctPeers);
    }

    private void Note(string key, object? value)
    {
        lock (_diagnosticsGate) _diagnostics[key] = value;
    }

    private void Try(string step, Action action)
    {
        try { action(); }
        catch (Exception e) { Note("cleanup_error_" + step, Describe(e)); }
    }

    private async Task TryAsync(string step, Func<Task> action)
    {
        try { await action().WaitAsync(_options.CleanupStepTimeout).ConfigureAwait(false); }
        catch (Exception e) { Note("cleanup_error_" + step, Describe(e)); }
    }

    private static async ValueTask SafeDisposeAsync(IAsyncDisposable disposable)
    {
        try { await disposable.DisposeAsync().ConfigureAwait(false); } catch { /* ignore while unwinding */ }
    }

    private static List<Dictionary<string, object?>> DescribeCandidates(IReadOnlyList<CandidateEndpoint> candidates)
        => candidates.Select(c => new Dictionary<string, object?>
        {
            ["type"] = CandidateTypeNames.ToWire(c.Type),
            ["ip"] = c.Ip,
            ["port"] = c.Port,
        }).ToList();

    // No secrets and no payloads in the text; the description is the exception's type and message only.
    private static string Describe(Exception e) => e switch
    {
        TimeoutException => "timeout",
        OperationCanceledException => "cancelled",
        MuxClosedException m => $"mux_closed: {m.Message}",
        AuthenticationException a => $"tls: {a.Message}",
        SocketException s => $"socket: {s.SocketErrorCode}",
        IOException io when io.InnerException is SocketException s => $"socket: {s.SocketErrorCode}",
        _ => $"{e.GetType().Name}: {e.Message}",
    };
}
