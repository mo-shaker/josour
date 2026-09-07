using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Josour.Core.Allowlist;
using Josour.Core.Browser;
using Josour.Core.Control;
using Josour.Core.Session;
using Josour.Core.Tunnel;
using Josour.Infrastructure.Api;
using Josour.Infrastructure.Control;
using Josour.Infrastructure.Diagnostics;

namespace Josour.Infrastructure.Session;

/// <summary>
/// Owns the whole client-side session: it listens to <see cref="IControlChannel.MessageReceived"/>, drives
/// <see cref="SessionPhaseTracker"/> (Idle → RequestPending → Preparing → Connecting → Active → Ending → Ended) and — from
/// week 4 on — the real tunnel and the work browser behind it.
///
/// <para>End to end, per role (docs/ws-protocol.md sections 3-5, docs/protocol.md sections 2 and 7):</para>
/// <list type="number">
///   <item><c>session.created</c> → build <see cref="SessionMaterial"/> (id, role, the 32-byte secret, expires_at,
///     same_public_ip, peer_public_ip), fetch the allow-list at the message's <c>allowlist_version</c>, create the tunnel
///     through <see cref="ITunnelSessionFactory"/>, <c>PrepareAsync</c>, send <c>session.endpoint</c>.</item>
///   <item><c>session.peer_endpoint</c> → <c>ConnectAsync</c>; the host reports <c>session.connected</c>, either side reports
///     <c>session.connect_failed</c> with the tunnel's diagnostics and tears down.</item>
///   <item><c>session.active</c> → guest: launch the work browser on <c>Proxy.ProbeUrl</c> and wait for the probe (skipped
///     when there is no browser to launch — see <see cref="NoWorkBrowser"/>); host: report <c>session.stats</c> every 30 s.
///     Both: a monotonic countdown to <c>expires_at</c>.</item>
///   <item>Any ending funnels through the single idempotent <see cref="EndSessionAsync(TunnelEndReason)"/>:
///     cleanup in the order of docs/protocol.md section 7 (the browser is closed by the tunnel's own step 2), then
///     <c>session.end</c> — except when the server already ended it (<c>session.terminate</c>) or we answered
///     <c>session.connect_failed</c>.</item>
/// </list>
///
/// <para>
/// The secret never leaves this class as anything but the byte array handed to the tunnel (which zeroes it during cleanup);
/// no log line, diagnostic or property carries it.
/// </para>
/// </summary>
public sealed class SessionCoordinator : INotifyPropertyChanged, IDisposable
{
    /// <summary>Local failure codes of <see cref="RequestSessionAsync"/> (the server's own codes are in <c>ControlErrorCodes</c>).</summary>
    public const string NotConnectedCode = "not_connected";
    public const string BusyCode = "busy";
    public const string TimeoutCode = "timeout";

    private static readonly IReadOnlyList<int> DefaultAllowedPorts = new[] { 80, 443 };

    private readonly IControlChannel _channel;
    private readonly IApiClient _api;
    private readonly ITunnelSessionFactory _tunnels;
    private readonly IWorkBrowser _browser;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly SessionCoordinatorOptions _options;
    private readonly SessionPhaseTracker _tracker = new();

    private readonly object _gate = new();
    private readonly object _stepGate = new();
    private readonly object _allowlistGate = new();

    private SessionRun? _run;
    private Task _steps = Task.CompletedTask;
    private IAllowlist? _allowlist;
    private HelloAckMessage? _hello;
    private TimeSpan _serverClockOffset = TimeSpan.Zero;
    private bool _disposed;

    private SessionPhase _phase = SessionPhase.Idle;
    private SessionInfo? _currentSession;
    private string? _lastEndReason;
    private TimeSpan? _timeRemaining;
    private long _bytesUp;
    private long _bytesDown;
    private string? _probeUrl;
    private bool _canReopenBrowser;
    private BrowserLaunchFailure? _browserFailure;
    private string? _browserFailureDetail;

    public SessionCoordinator(
        IControlChannel channel,
        IApiClient api,
        ITunnelSessionFactory tunnels,
        IWorkBrowser browser,
        ILogger<SessionCoordinator>? logger = null,
        TimeProvider? time = null,
        SessionCoordinatorOptions? options = null)
    {
        _channel = channel ?? throw new ArgumentNullException(nameof(channel));
        _api = api ?? throw new ArgumentNullException(nameof(api));
        _tunnels = tunnels ?? throw new ArgumentNullException(nameof(tunnels));
        _browser = browser ?? throw new ArgumentNullException(nameof(browser));
        _logger = (ILogger?)logger ?? NullLogger.Instance;
        _time = time ?? TimeProvider.System;
        _options = options ?? SessionCoordinatorOptions.Default;

        _tracker.PhaseChanged += OnTrackerPhaseChanged;
        _channel.MessageReceived += OnMessage;
        _channel.StateChanged += OnChannelStateChanged;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public SessionPhase Phase { get => _phase; private set => SetField(ref _phase, value); }

    public SessionInfo? CurrentSession { get => _currentSession; private set => SetField(ref _currentSession, value); }

    /// <summary>Why the last request or session ended (a wire reason, an error code or a <c>request.result.reason</c>).</summary>
    public string? LastEndReason { get => _lastEndReason; private set => SetField(ref _lastEndReason, value); }

    /// <summary>Monotonic time left until <c>expires_at</c> while the session is active; null otherwise.</summary>
    public TimeSpan? TimeRemaining { get => _timeRemaining; private set => SetField(ref _timeRemaining, value); }

    /// <summary>Approximate payload sent through the tunnel, refreshed on every tick and frozen at the end for the summary.</summary>
    public long BytesUp { get => _bytesUp; private set => SetField(ref _bytesUp, value); }

    public long BytesDown { get => _bytesDown; private set => SetField(ref _bytesDown, value); }

    /// <summary>The guest's probe page (also the page the work browser opens); null on the host or before the tunnel is up.</summary>
    public string? ProbeUrl { get => _probeUrl; private set => SetField(ref _probeUrl, value); }

    /// <summary>The guest may reopen the work browser (it is up, and the user may have closed the window).</summary>
    public bool CanReopenBrowser { get => _canReopenBrowser; private set => SetField(ref _canReopenBrowser, value); }

    /// <summary>Why the work browser could not be used (managed policy / handoff / not found / probe timeout); null when it is fine.</summary>
    public BrowserLaunchFailure? BrowserFailure { get => _browserFailure; private set => SetField(ref _browserFailure, value); }

    /// <summary>Technical detail behind <see cref="BrowserFailure"/> for the log and the details line; never user-facing prose.</summary>
    public string? BrowserFailureDetail { get => _browserFailureDetail; private set => SetField(ref _browserFailureDetail, value); }

    /// <summary>The pending request id while <see cref="SessionPhase.RequestPending"/>.</summary>
    public Guid? PendingRequestId => _tracker.PendingRequestId;

    /// <summary>The last <c>hello.ack</c> settings this coordinator saw (allowed ports, log_domains, max session minutes).</summary>
    public ServerSettings? ServerSettings => _hello?.Settings;

    /// <summary>True while a session exists that has to be cleaned up before the app may exit.</summary>
    public bool HasLiveSession => _tracker.Phase is SessionPhase.Preparing or SessionPhase.Connecting or SessionPhase.Active;

    // ---------- guest: the request ----------

    /// <summary>
    /// Guest: <c>request.create</c> and wait for <c>request.created</c> with the same <c>ref</c>.
    /// Returns null when the request is pending on the server, otherwise the error code to show
    /// (a <c>ControlErrorCodes</c> value, or one of <see cref="NotConnectedCode"/> / <see cref="BusyCode"/> / <see cref="TimeoutCode"/>).
    /// </summary>
    public async Task<string?> RequestSessionAsync(Guid hostDeviceId, int durationMin, CancellationToken ct)
    {
        var requestRef = ControlRef.Next();
        if (!_tracker.TrackRequest(requestRef))
        {
            _logger.LogWarning("request.create ignored: phase is {Phase}", _tracker.Phase);
            return BusyCode;
        }

        try
        {
            var reply = await _channel.RequestAsync(new RequestCreateMessage(requestRef, hostDeviceId, durationMin), _options.RequestReplyTimeout, ct).ConfigureAwait(false);

            // The real channel throws ControlErrorException; MockControlChannel still answers with the frame itself.
            if (reply is ErrorMessage error)
            {
                _logger.LogWarning("request.create rejected: {Code} {Message}", error.Code, error.Message); // tracker already returned to Idle via OnMessage
                return error.Code;
            }

            _logger.LogInformation(
                "request.create accepted by the server: request {RequestId} for {DurationMin} min",
                (reply as RequestCreatedMessage)?.RequestId,
                durationMin);
            return null;
        }
        catch (ControlErrorException ex)
        {
            _logger.LogWarning("request.create rejected: {Code} {Message}", ex.Code, ex.Message);
            _tracker.Reset(); // the matching error frame also reaches the tracker; both land on Idle
            return ex.Code;
        }
        catch (Exception ex) when (ex is TimeoutException or InvalidOperationException or ControlChannelClosedException)
        {
            _logger.LogWarning(ex, "request.create failed");
            _tracker.Reset();
            return ex is TimeoutException ? TimeoutCode : NotConnectedCode;
        }
    }

    /// <summary>Guest: <c>request.cancel</c> for the pending request.</summary>
    public Task CancelRequestAsync(CancellationToken ct)
    {
        if (_tracker.PendingRequestId is not Guid requestId)
        {
            return Task.CompletedTask;
        }

        return _channel.SendAsync(new RequestCancelMessage(ControlRef.Next(), requestId), ct);
    }

    // ---------- host: the answer ----------

    public Task AcceptRequestAsync(Guid requestId, CancellationToken ct) =>
        _channel.SendAsync(new RequestAcceptMessage(ControlRef.Next(), requestId), ct);

    public Task RejectRequestAsync(Guid requestId, CancellationToken ct) =>
        _channel.SendAsync(new RequestRejectMessage(ControlRef.Next(), requestId), ct);

    // ---------- both: ending ----------

    /// <summary>
    /// The single, idempotent ending. Runs the cleanup order of docs/protocol.md section 7 through
    /// <see cref="ITunnelSession.EndAsync"/> (which closes the work browser in its step 2) and then reports
    /// <c>session.end</c>. Two triggers at the same time produce exactly one <c>session.end</c>.
    /// </summary>
    public Task EndSessionAsync(TunnelEndReason reason) => EndCoreAsync(reason, notifyServer: true);

    /// <summary>The user pressed Disconnect: end with this role's reason (<c>guest_ended</c> / <c>host_ended</c>).</summary>
    public Task DisconnectAsync()
    {
        var run = CurrentRun;
        var role = run?.Role ?? (_tracker.CurrentSession?.IsHost == true ? TunnelRole.Host : TunnelRole.Guest);
        return EndSessionAsync(SessionEndReasonNames.LocalEnd(role));
    }

    /// <summary>App shutdown: end a live session and finish the cleanup before the process goes away.</summary>
    public async Task ShutdownAsync(TimeSpan timeout)
    {
        var run = CurrentRun;
        if (run is null)
        {
            await SafeAsync("browser close", () => CloseBrowserAsync(CancellationToken.None)).ConfigureAwait(false);
            return;
        }

        _logger.LogInformation("Shutting down with a live session {SessionId}: ending it first", run.SessionId);
        var ending = EndSessionAsync(SessionEndReasonNames.LocalEnd(run.Role));
        try
        {
            await ending.WaitAsync(timeout).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            _logger.LogWarning("Session cleanup did not finish within {Timeout}; exiting anyway", timeout);
        }
    }

    /// <summary>The UI dismissed the end-of-session summary: back to Idle.</summary>
    public void Acknowledge()
    {
        _tracker.Reset();
        Publish(() =>
        {
            TimeRemaining = null;
            BytesUp = 0;
            BytesDown = 0;
            ProbeUrl = null;
            CanReopenBrowser = false;
            BrowserFailure = null;
            BrowserFailureDetail = null;
        });
    }

    /// <summary>
    /// Guest: reopen the work browser on the probe page (the user closed the window but the session is still up).
    /// Returns null when there is nothing to reopen.
    /// </summary>
    public async Task<BrowserLaunchResult?> ReopenBrowserAsync(CancellationToken ct)
    {
        var run = CurrentRun;
        if (run is null || run.IsHost || !_browser.CanLaunch || run.Tunnel?.Proxy is not { } proxy || _tracker.Phase != SessionPhase.Active)
        {
            return null;
        }

        _logger.LogInformation("Reopening the work browser for session {SessionId}", run.SessionId);
        var result = await _browser.OpenAsync(proxy.Port, proxy.ProbeUrl, ct).ConfigureAwait(false);
        Publish(() =>
        {
            BrowserFailure = result.Success ? null : result.Failure;
            BrowserFailureDetail = result.Success ? null : result.Detail;
        });
        return result;
    }

    // ---------- inbound ----------

    private void OnMessage(ControlMessage message)
    {
        // Before the tracker moves to Active: the session made it in time, so the connect watchdog is disarmed here rather
        // than in the (asynchronous) step, which keeps "Active" and "the deadline is gone" a single observable moment.
        if (message is SessionActiveMessage active && CurrentRun is { } running && running.SessionId == active.SessionId)
        {
            running.StopConnectDeadline();
        }

        var changed = _tracker.Apply(message);

        switch (message)
        {
            case HelloAckMessage m:
                _hello = m;
                _serverClockOffset = m.ServerTime - _time.GetUtcNow();
                if (_serverClockOffset.Duration() > _options.ClockSkewWarning)
                {
                    // The countdown is immune to this (it is anchored on server time and then counted monotonically —
                    // plan 8.5 "الوقت"), but a clock this far out breaks TLS validity windows and makes every log line
                    // on this machine hard to line up with the server's, so it is worth saying out loud once per connect.
                    _logger.LogWarning(
                        "This machine's clock is {OffsetSeconds:0.#} s away from the server's ({ServerTime:O}); the session countdown follows the server, but check the system clock",
                        _serverClockOffset.TotalSeconds,
                        m.ServerTime);
                }

                _logger.LogDebug(
                    "hello.ack: max_session_minutes={MaxMinutes} allowed_ports={AllowedPorts} log_domains={LogDomains} allowlist_version={AllowlistVersion} clock_offset={OffsetMs} ms",
                    m.Settings.MaxSessionMinutes,
                    string.Join(",", m.Settings.AllowedPorts),
                    m.Settings.LogDomains,
                    m.AllowlistVersion,
                    (long)_serverClockOffset.TotalMilliseconds);
                break;

            case SessionCreatedMessage m when _tracker.CurrentSession?.SessionId == m.SessionId:
                _logger.LogInformation(
                    "session.created {SessionId}: role={Role} peer={PeerName}/{PeerDevice} peer_public_ip={PeerIp} same_public_ip={SameIp} expires_at={ExpiresAt:O} allowlist_version={Allowlist}",
                    m.SessionId, m.Role, m.Peer.UserDisplayName, m.Peer.DeviceName, m.PeerPublicIp, m.SamePublicIp, m.ExpiresAt, m.AllowlistVersion);
                StartRun(m);
                break;

            case SessionPeerEndpointMessage m when changed:
                _logger.LogInformation("session.peer_endpoint {SessionId}: {CandidateCount} candidates", m.SessionId, m.Candidates.Count);
                if (CurrentRun is { } dialling && dialling.SessionId == m.SessionId)
                {
                    dialling.PeerEndpointSeen = true; // from here ConnectAsync owns the remaining budget
                }

                Enqueue(run => ConnectRunAsync(run, m), m.SessionId);
                break;

            case SessionActiveMessage m when changed:
                _logger.LogInformation("session.active {SessionId}: expires_at={ExpiresAt:O}", m.SessionId, m.ExpiresAt);
                Enqueue(run => ActivateRunAsync(run, m), m.SessionId);
                break;

            case SessionTerminateMessage m:
                // The server already ended it (expiry, admin, the peer's disconnect): clean up locally, never answer session.end.
                _logger.LogInformation("session.terminate {SessionId}: reason={Reason}", m.SessionId, m.Reason);
                if (CurrentRun?.SessionId == m.SessionId || _tracker.CurrentSession?.SessionId == m.SessionId)
                {
                    _ = EndCoreAsync(SessionEndReasonNames.Parse(m.Reason), notifyServer: false);
                }

                break;

            case RequestResultMessage m when m.Accepted:
                // Stay in RequestPending: session.created carries the details and moves us to Preparing.
                _logger.LogInformation("request.result {RequestId}: accepted (session {SessionId})", m.RequestId, m.SessionId);
                break;

            case RequestResultMessage m:
                _logger.LogInformation("request.result {RequestId}: not accepted ({Reason})", m.RequestId, m.Reason);
                break;

            case RequestExpiredMessage m:
                _logger.LogInformation("request.expired {RequestId}", m.RequestId);
                break;

            case ErrorMessage m when m.Ref is null:
                _logger.LogWarning("Server error without ref: {Code} {Message}", m.Code, m.Message);
                break;
        }
    }

    /// <summary>
    /// The channel dropped: a request that was waiting for an answer cannot be answered any more, and the server ends an
    /// unfinished session of this device immediately (docs/ws-protocol.md section 1), so the local side goes away too.
    /// </summary>
    private void OnChannelStateChanged(ControlChannelState state)
    {
        if (state == ControlChannelState.Connected)
        {
            return;
        }

        if (_tracker.Phase == SessionPhase.RequestPending)
        {
            _logger.LogInformation("Control channel is {State}: dropping the pending request", state);
            _tracker.Reset();
            return;
        }

        if (CurrentRun is { } run && HasLiveSession)
        {
            _logger.LogWarning("Control channel is {State} while session {SessionId} is live: tearing it down", state, run.SessionId);
            // The socket is gone, so session.end cannot be delivered; the server ends it with guest_/host_disconnected itself.
            _ = EndCoreAsync(SessionEndReasonNames.LocalDisconnect(run.Role), notifyServer: false);
        }
    }

    // ---------- 1. session.created ----------

    private void StartRun(SessionCreatedMessage message)
    {
        var role = string.Equals(message.Role, SessionInfo.HostRole, StringComparison.Ordinal) ? TunnelRole.Host : TunnelRole.Guest;
        var run = new SessionRun(message.SessionId, role, message.AllowlistVersion, message.ExpiresAt, _time.GetTimestamp());
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _run = run;
        }

        Publish(() =>
        {
            BytesUp = 0;
            BytesDown = 0;
            TimeRemaining = null;
            ProbeUrl = null;
            CanReopenBrowser = false;
            BrowserFailure = null;
            BrowserFailureDetail = null;
        });

        // docs/ws-protocol.md section 5: the server ends a session that is not active 30 s after session.created. Without a
        // local deadline a peer that never answers with session.endpoint would leave this side waiting in Preparing until the
        // server's session.terminate; instead we report session.connect_failed with our diagnostics and tear down.
        run.ConnectDeadline = _time.CreateTimer(_ => OnConnectDeadline(run), null, _options.ConnectTimeout, Timeout.InfiniteTimeSpan);

        Enqueue(r => PrepareRunAsync(r, message), message.SessionId);
    }

    private void OnConnectDeadline(SessionRun run)
    {
        try
        {
            run.StopConnectDeadline();
            if (run.PeerEndpointSeen || run.Cts.IsCancellationRequested || !ReferenceEquals(CurrentRun, run))
            {
                return; // ConnectAsync has its own share of the budget, or the session is already over
            }

            _logger.LogError("No session.peer_endpoint for {SessionId} within {Timeout}: reporting connect_failed", run.SessionId, _options.ConnectTimeout);
            _ = ReportConnectFailedAsync(run, "peer_endpoint_timeout");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "The connect deadline of session {SessionId} failed", run.SessionId);
        }
    }

    private async Task PrepareRunAsync(SessionRun run, SessionCreatedMessage message)
    {
        byte[]? secret = null;
        try
        {
            secret = DecodeSecret(message.SecretB64);
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException)
        {
            _logger.LogError("session.created {SessionId}: secret_b64 is not 32 usable bytes; ending with protocol_error", run.SessionId);
            await EndCoreAsync(TunnelEndReason.ProtocolError, notifyServer: true).ConfigureAwait(false);
            return;
        }

        try
        {
            var allowlist = await GetAllowlistAsync(message.AllowlistVersion, run.Cts.Token).ConfigureAwait(false);
            var material = new SessionMaterial(run.SessionId, run.Role, secret, message.ExpiresAt, message.SamePublicIp, message.PeerPublicIp);

            ITunnelSession tunnel;
            try
            {
                // From here on the tunnel owns the secret and zeroes it during cleanup (docs/protocol.md section 7 step 5).
                tunnel = _tunnels.Create(new TunnelSessionRequest(
                    material,
                    allowlist,
                    _hello?.Settings.AllowedPorts ?? DefaultAllowedPorts,
                    _hello?.PublicIp,
                    _browser,
                    CloseBrowserAsync));
            }
            catch
            {
                CryptographicOperations.ZeroMemory(material.Secret);
                throw;
            }
            finally
            {
                secret = null;
            }

            run.Tunnel = tunnel;
            tunnel.ProbeSeen += run.OnProbeSeen;
            tunnel.Died += reason => OnTunnelDied(run, reason);
            if (run.Cts.IsCancellationRequested)
            {
                return; // the session ended while we were building it; EndCoreAsync picks the tunnel up from run.Tunnel
            }

            var local = await tunnel.PrepareAsync(run.Cts.Token).ConfigureAwait(false);
            _logger.LogInformation(
                "Tunnel prepared for {SessionId}: {CandidateCount} candidates, certificate {Fingerprint}",
                run.SessionId, local.Candidates.Count, local.CertFingerprintSha256Hex);

            await _channel.SendAsync(
                new SessionEndpointMessage(run.SessionId, local.CertFingerprintSha256Hex, ToWire(local.Candidates)),
                run.Cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (run.Cts.IsCancellationRequested)
        {
            _logger.LogInformation("Preparing session {SessionId} was cancelled by the ending", run.SessionId);
        }
        catch (AllowlistUnavailableException ex)
        {
            // A named failure, not a stack trace: the peer and the server learn the session was refused because the list
            // it was approved under could not be read, which is a different thing from a network that would not connect.
            _logger.LogError(
                "Session {SessionId} refused: allow-list version {Version} could not be loaded ({Detail})",
                run.SessionId, ex.Version, ex.Message);
            await ReportConnectFailedAsync(run, AllowlistUnavailableException.FailureReason).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Preparing session {SessionId} failed", run.SessionId);
            await ReportConnectFailedAsync(run, ex.GetType().Name + ": " + ex.Message).ConfigureAwait(false);
        }
        finally
        {
            if (secret is not null)
            {
                CryptographicOperations.ZeroMemory(secret);
            }
        }
    }

    // ---------- 2. session.peer_endpoint ----------

    private async Task ConnectRunAsync(SessionRun run, SessionPeerEndpointMessage message)
    {
        if (run.Tunnel is not { } tunnel)
        {
            _logger.LogWarning("session.peer_endpoint {SessionId} arrived without a prepared tunnel", run.SessionId);
            await ReportConnectFailedAsync(run, "the tunnel was not prepared").ConfigureAwait(false);
            return;
        }

        var candidates = new List<CandidateEndpoint>(message.Candidates.Count);
        foreach (var dto in message.Candidates)
        {
            if (CandidateTypeNames.TryParse(dto.Type, out var type))
            {
                candidates.Add(new CandidateEndpoint(type, dto.Ip, dto.Port));
            }
            else
            {
                _logger.LogWarning("Ignoring peer candidate of unknown type '{Type}'", dto.Type);
            }
        }

        var timeout = RemainingConnectBudget(run);
        try
        {
            var result = await tunnel.ConnectAsync(
                new PeerEndpointInfo(message.CertFpSha256, candidates),
                timeout,
                run.Cts.Token).ConfigureAwait(false);

            if (!result.Connected)
            {
                _logger.LogWarning("Tunnel connect failed for {SessionId} after {ConnectMs} ms: {Reason}", run.SessionId, result.ConnectMs, result.FailureReason);
                await ReportConnectFailedAsync(run, result.FailureReason).ConfigureAwait(false);
                return;
            }

            run.Connected = true;
            _logger.LogInformation(
                "Tunnel connected for {SessionId}: winner={Winner} connect_ms={ConnectMs} tls={Tls}",
                run.SessionId, result.WinnerType, result.ConnectMs, result.TlsVersion);

            if (run.IsHost)
            {
                // docs/ws-protocol.md section 5: session.connected is the host's word only; the guest would get error(forbidden).
                await _channel.SendAsync(
                    new SessionConnectedMessage(
                        run.SessionId,
                        result.WinnerType is { } winner ? CandidateTypeNames.ToWire(winner) : string.Empty,
                        result.ConnectMs,
                        result.TlsVersion ?? string.Empty),
                    run.Cts.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (run.Cts.IsCancellationRequested)
        {
            _logger.LogInformation("Connecting session {SessionId} was cancelled by the ending", run.SessionId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Connecting session {SessionId} failed", run.SessionId);
            await ReportConnectFailedAsync(run, ex.GetType().Name + ": " + ex.Message).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// <c>session.connect_failed</c> with the tunnel's per-candidate diagnostics, then a local teardown: the server ends the
    /// session with <c>connect_failed</c> when it sees this frame, so no <c>session.end</c> follows.
    /// </summary>
    private async Task ReportConnectFailedAsync(SessionRun run, string? failureReason)
    {
        if (run.Cts.IsCancellationRequested)
        {
            return;
        }

        var diagnostics = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (run.Tunnel is { } tunnel)
        {
            foreach (var pair in tunnel.Diagnostics)
            {
                diagnostics[pair.Key] = pair.Value;
            }
        }

        if (failureReason is not null)
        {
            diagnostics["failure_reason"] = failureReason;
        }

        await SafeAsync("session.connect_failed", () => _channel.SendAsync(new SessionConnectFailedMessage(run.SessionId, diagnostics), CancellationToken.None)).ConfigureAwait(false);
        await EndCoreAsync(TunnelEndReason.ConnectFailed, notifyServer: false).ConfigureAwait(false);
    }

    // ---------- 3. session.active ----------

    private async Task ActivateRunAsync(SessionRun run, SessionActiveMessage message)
    {
        run.ExpiresAt = message.ExpiresAt;
        run.CountdownAnchor = _time.GetTimestamp();
        run.RemainingAtStart = message.ExpiresAt - ServerNow();
        run.LastStats = _time.GetTimestamp();
        StartTicker(run);
        Tick(run);

        if (run.IsHost)
        {
            _logger.LogInformation("Session {SessionId} is active on the host: reporting session.stats every {Interval}", run.SessionId, _options.StatsInterval);
            return;
        }

        if (!run.Connected || run.Tunnel?.Proxy is not { } proxy)
        {
            _logger.LogError("session.active {SessionId} but the guest tunnel is not connected", run.SessionId);
            await ReportConnectFailedAsync(run, "session.active without a connected tunnel").ConfigureAwait(false);
            return;
        }

        Publish(() => ProbeUrl = proxy.ProbeUrl);

        if (!_browser.CanLaunch)
        {
            // Head-less (see NoWorkBrowser): there is no browser to launch, so nothing could bypass the proxy and
            // browser_not_proxied would name a browser that was never started. The proxy is up on ProbeUrl and the
            // session runs on; whatever the caller sends through it is what verifies the proxy.
            _logger.LogInformation(
                "Session {SessionId} is active without a work browser: the local proxy is on 127.0.0.1:{ProxyPort} ({ProbeUrl}); the probe wait is skipped",
                run.SessionId, proxy.Port, proxy.ProbeUrl);
            return;
        }

        var launch = await _browser.OpenAsync(proxy.Port, proxy.ProbeUrl, run.Cts.Token).ConfigureAwait(false);
        if (!launch.Success)
        {
            _logger.LogError("The work browser did not start ({Failure}): {Detail}", launch.Failure, launch.Detail);
            Publish(() =>
            {
                BrowserFailure = launch.Failure ?? BrowserLaunchFailure.Other;
                BrowserFailureDetail = launch.Detail;
            });
            await EndCoreAsync(TunnelEndReason.BrowserNotProxied, notifyServer: true).ConfigureAwait(false);
            return;
        }

        Publish(() => CanReopenBrowser = true);

        // docs/ws-protocol.md section 5: no probe page through our proxy = the browser is not proxied.
        var seen = await WaitForProbeAsync(run).ConfigureAwait(false);
        if (run.Cts.IsCancellationRequested)
        {
            return; // the session ended for another reason while we were waiting
        }

        if (seen)
        {
            _logger.LogInformation("The work browser reached the probe page: session {SessionId} is fully active", run.SessionId);
            return;
        }

        _logger.LogError("No probe page within {Timeout}: the work browser is not using the local proxy", _options.ProbeTimeout);
        Publish(() =>
        {
            BrowserFailure = BrowserLaunchFailure.ProbeTimeout;
            BrowserFailureDetail = $"no request for {proxy.ProbeUrl} through 127.0.0.1:{proxy.Port} within {_options.ProbeTimeout.TotalSeconds:0} s";
        });
        await EndCoreAsync(TunnelEndReason.BrowserNotProxied, notifyServer: true).ConfigureAwait(false);
    }

    private async Task<bool> WaitForProbeAsync(SessionRun run)
    {
        if (run.ProbeSeen.Task.IsCompletedSuccessfully)
        {
            return true;
        }

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(run.Cts.Token);
        var delay = Task.Delay(_options.ProbeTimeout, _time, stop.Token);
        try
        {
            await Task.WhenAny(run.ProbeSeen.Task, delay).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // the session ended while we were waiting
        }
        finally
        {
            stop.Cancel(); // stops the timer when the probe won the race
        }

        return run.ProbeSeen.Task.IsCompletedSuccessfully;
    }

    // ---------- the tick: countdown, stats, expiry ----------

    private void StartTicker(SessionRun run)
    {
        run.Ticker ??= _time.CreateTimer(_ => Tick(run), null, _options.Tick, _options.Tick);
    }

    private void Tick(SessionRun run)
    {
        try
        {
            if (run.Cts.IsCancellationRequested || !ReferenceEquals(CurrentRun, run))
            {
                return;
            }

            var stats = run.Tunnel?.Stats;
            var remaining = run.RemainingAtStart - _time.GetElapsedTime(run.CountdownAnchor);
            if (remaining < TimeSpan.Zero)
            {
                remaining = TimeSpan.Zero;
            }

            Publish(() =>
            {
                TimeRemaining = remaining;
                if (stats is not null)
                {
                    BytesUp = stats.BytesUp;
                    BytesDown = stats.BytesDown;
                }

                CanReopenBrowser = _browser.CanLaunch && !run.IsHost && run.Connected && run.Tunnel?.Proxy is not null && _tracker.Phase == SessionPhase.Active;
            });

            if (remaining <= TimeSpan.Zero)
            {
                // ننهي محليًا فورًا (لا نمرر بايتًا بعد انتهاء المدة) بلا session.end:
                // "expired" حكم من أحكام الخادم، ومؤقته مشتق من expires_at نفسه فيصدر session.terminate
                // خلال فارق الساعتين. إرساله من العميل يُرد bad_request (ws-protocol القسم 9).
                _logger.LogInformation("Session {SessionId} reached expires_at: tearing down locally, awaiting the server's session.terminate", run.SessionId);
                _ = EndCoreAsync(TunnelEndReason.Expired, notifyServer: false);
                return;
            }

            if (run.IsHost && stats is not null && _time.GetElapsedTime(run.LastStats) >= _options.StatsInterval)
            {
                run.LastStats = _time.GetTimestamp();
                _ = SafeAsync("session.stats", () => _channel.SendAsync(new SessionStatsMessage(run.SessionId, stats.BytesUp, stats.BytesDown), CancellationToken.None));
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "The session tick failed");
        }
    }

    private static void StopTicker(SessionRun run)
    {
        var ticker = run.Ticker;
        run.Ticker = null;
        ticker?.Dispose();
    }

    // ---------- 4. the single ending ----------

    private Task EndCoreAsync(TunnelEndReason reason, bool notifyServer)
    {
        var run = CurrentRun;
        if (run is null)
        {
            return Task.CompletedTask;
        }

        lock (run.EndGate)
        {
            // Idempotent: whoever gets here first decides the reason; later triggers await the same cleanup.
            run.EndTask ??= Task.Run(() => EndRunAsync(run, reason, notifyServer));
            return run.EndTask;
        }
    }

    private async Task EndRunAsync(SessionRun run, TunnelEndReason reason, bool notifyServer)
    {
        var wire = SessionEndReasonNames.ToWire(reason);
        if (notifyServer && IsServerVerdict(reason))
        {
            // docs/ws-protocol.md section 9: expired / connect_failed / admin_terminated are the server's own verdicts and it
            // answers bad_request to a client that claims one. We tear down locally and let its session.terminate arrive.
            _logger.LogInformation("Session {SessionId} ends with '{Reason}', which only the server may pronounce: no session.end", run.SessionId, wire);
            notifyServer = false;
        }

        _logger.LogInformation("Ending session {SessionId}: reason={Reason} notify_server={Notify}", run.SessionId, wire, notifyServer);
        try
        {
            _tracker.BeginEnding(wire); // false when session.terminate already moved the tracker to Ending
            run.Cts.Cancel();
            StopTicker(run);

            long bytesUp = 0;
            long bytesDown = 0;
            IReadOnlyList<string> domains = Array.Empty<string>();
            ListenerAuthReport? listenerHits = null;

            if (run.Tunnel is { } tunnel)
            {
                tunnel.ProbeSeen -= run.OnProbeSeen;

                // docs/protocol.md section 7 steps 1-5: stop accepting, close the browser (our hook), GOAWAY, listener + UPnP,
                // certificate + secret. Step 6 (session.end) is ours, below.
                await SafeAsync("tunnel end", () => tunnel.EndAsync(reason, CancellationToken.None).WaitAsync(_options.CleanupTimeout)).ConfigureAwait(false);

                var stats = tunnel.Stats;
                bytesUp = stats.BytesUp;
                bytesDown = stats.BytesDown;
                domains = tunnel.DomainsSeen.ToArray();

                // Read before Dispose: the counters live on the tunnel, and the listener is already gone by now.
                if (ListenerAuthDiagnostics.TryRead(tunnel.Diagnostics, out var hits))
                {
                    listenerHits = hits;
                }

                await SafeAsync("tunnel dispose", () => tunnel.DisposeAsync().AsTask().WaitAsync(_options.CleanupTimeout)).ConfigureAwait(false);
            }

            // Safety net: with no tunnel (or a cleanup that threw) the browser would otherwise stay open on the proxy.
            await SafeAsync("browser close", () => CloseBrowserAsync(CancellationToken.None)).ConfigureAwait(false);

            var reported = ReportedDomains(run, domains);
            Publish(() =>
            {
                BytesUp = bytesUp;
                BytesDown = bytesDown;
                TimeRemaining = null;
                ProbeUrl = null;
                CanReopenBrowser = false;
            });

            if (notifyServer)
            {
                await SafeAsync("session.end", () => SendSessionEndAsync(run, wire, bytesUp, bytesDown, reported)).ConfigureAwait(false);
            }

            if (listenerHits is not null)
            {
                await SafeAsync("POST /diagnostics", () => ReportListenerHitsAsync(run, listenerHits)).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Session cleanup failed for {SessionId}", run.SessionId);
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_run, run))
                {
                    _run = null;
                }
            }

            run.Dispose();
            _tracker.MarkEnded();
        }
    }

    private Task SendSessionEndAsync(SessionRun run, string wire, long bytesUp, long bytesDown, IReadOnlyList<string> domains)
    {
        if (_channel.State != ControlChannelState.Connected)
        {
            _logger.LogWarning("session.end for {SessionId} not sent: the control channel is {State}", run.SessionId, _channel.State);
            return Task.CompletedTask;
        }

        _logger.LogInformation(
            "session.end {SessionId}: reason={Reason} bytes_up={BytesUp} bytes_down={BytesDown} domains={DomainCount}",
            run.SessionId, wire, bytesUp, bytesDown, domains.Count);
        return _channel.SendAsync(new SessionEndMessage(run.SessionId, wire, bytesUp, bytesDown, domains), CancellationToken.None);
    }

    /// <summary>
    /// <c>POST /api/v1/diagnostics</c> with the reserved keys of docs/api.md when the tunnel's listener was knocked on by
    /// something that could not authenticate. The server turns a positive <c>listener_unauthenticated</c> into a
    /// <c>security_events</c> row, because that moment — a connection reaching the host's port during the connect window
    /// and failing <c>AUTH1</c> — is invisible to it otherwise.
    /// <para>
    /// It runs after <c>session.end</c> and on its own short budget: this is a report about a session that is already over,
    /// so it may cost the ending nothing. A failure is logged and dropped (<see cref="SafeAsync"/> wraps the call).
    /// </para>
    /// </summary>
    private async Task ReportListenerHitsAsync(SessionRun run, ListenerAuthReport report)
    {
        var data = report.ToDiagnosticsData();
        _logger.LogWarning(
            "{Count} connection(s) reached the tunnel listener of session {SessionId} without passing AUTH1 from {PeerCount} address(es); reporting to the server",
            report.UnauthenticatedCount,
            run.SessionId,
            report.Peers.Count);

        using var budget = new CancellationTokenSource(_options.DiagnosticsTimeout);
        var accepted = await _api.PostDiagnosticsAsync(
            run.SessionId,
            run.IsHost ? SessionInfo.HostRole : SessionInfo.GuestRole,
            data,
            budget.Token).ConfigureAwait(false);
        _logger.LogInformation("Listener diagnostics stored as {DiagnosticsId}", accepted.Id);
    }

    /// <summary>Domains are the host's observation and are only reported when the server asks for them (<c>settings.log_domains</c>).</summary>
    private IReadOnlyList<string> ReportedDomains(SessionRun run, IReadOnlyList<string> domains)
    {
        if (!run.IsHost)
        {
            return Array.Empty<string>();
        }

        if (_hello?.Settings.LogDomains != true)
        {
            if (domains.Count > 0)
            {
                _logger.LogInformation("log_domains is off: {DomainCount} domains stay on this device", domains.Count);
            }

            return Array.Empty<string>();
        }

        return domains;
    }

    /// <summary>
    /// The tunnel died while both control channels are up — the one moment the server cannot see by itself, so we report it.
    /// The tunnel names the PEER (host → <c>guest_disconnected</c>, guest → <c>host_disconnected</c>), which is exactly the
    /// direction docs/ws-protocol.md section 9 requires. A suggestion that is a server verdict (a peer GOAWAY(expired)) is
    /// turned into a local teardown by <see cref="EndRunAsync"/>.
    /// </summary>
    private void OnTunnelDied(SessionRun run, TunnelEndReason suggested)
    {
        _logger.LogWarning("The tunnel of session {SessionId} died; suggested reason {Reason}", run.SessionId, suggested);
        _ = EndCoreAsync(suggested, notifyServer: true);
    }

    /// <summary>docs/ws-protocol.md section 9: reasons a client may never put in <c>session.end</c>.</summary>
    private static bool IsServerVerdict(TunnelEndReason reason)
        => reason is TunnelEndReason.Expired or TunnelEndReason.ConnectFailed or TunnelEndReason.AdminTerminated;

    private Task CloseBrowserAsync(CancellationToken ct) => _browser.CloseAsync(_options.BrowserCloseGrace, ct);

    // ---------- allow-list ----------

    /// <summary>
    /// The allow-list at EXACTLY the version <c>session.created</c> named, cached by version.
    /// <para>
    /// Week 6: this asks <c>GET /domains?version=N</c>, not <c>GET /domains</c>. Until the retention guarantee was written
    /// down (docs/api.md: versions are immutable snapshots and are never pruned) the only safe fetch was "the current list",
    /// and a version mismatch could only be logged. It is now safe to depend on the exact version, and the difference is not
    /// cosmetic: the guest was told which sites the session grants and the host approved that same list, so running the
    /// tunnel against a list the admin has changed since would enforce something nobody agreed to — in either direction.
    /// </para>
    /// <para>
    /// <b>When that version cannot be fetched, the session does not start.</b> This is the same principle as the host's
    /// pre-accept disclosure (<see cref="AllowlistDisclosure"/>): a list that could not be loaded is not an empty list, and
    /// the two must never be treated alike. The disclosure can afford to say "could not be loaded" and let a human decide,
    /// because nothing is enforced by a screen. The tunnel has no such option — it is the thing doing the enforcing — and
    /// both fallbacks available to it are wrong: the current list would enforce rules the parties never saw, and an empty
    /// list would silently deny everything, leaving the user in a session where nothing loads and nothing says why. Failing
    /// the session with a stated reason (<c>allowlist_unavailable</c>) is the only answer that is neither a silent
    /// over-permission nor a silent denial.
    /// </para>
    /// </summary>
    /// <exception cref="AllowlistUnavailableException">The exact version could not be fetched.</exception>
    private async Task<IAllowlist> GetAllowlistAsync(int version, CancellationToken ct)
    {
        IAllowlist? cached;
        lock (_allowlistGate)
        {
            cached = _allowlist;
        }

        if (cached is not null && cached.Version == version)
        {
            return cached;
        }

        if (version < 1)
        {
            // The server never sends this; a malformed frame must not turn into a request for "?version=0".
            throw new AllowlistUnavailableException(version, $"'{version}' is not an allow-list version.");
        }

        DomainsDto dto;
        try
        {
            dto = await _api.GetDomainsVersionAsync(version, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is ApiException or ApiUnavailableException)
        {
            _logger.LogError(ex, "Could not load allow-list version {Version}: the session cannot be policed and will not start", version);
            throw new AllowlistUnavailableException(version, ex.Message, ex);
        }

        if (dto.Version != version)
        {
            // A server contradicting itself is not a list we may enforce (retention says this cannot happen).
            _logger.LogError("GET /domains?version={Wanted} answered with version {Got}: refusing to run the session on it", version, dto.Version);
            throw new AllowlistUnavailableException(version, $"the server answered with version {dto.Version}");
        }

        var entries = new List<AllowlistEntry>(dto.Entries.Count);
        foreach (var raw in dto.Entries)
        {
            if (AllowlistMatcher.TryParseEntry(raw, out var entry, out var error))
            {
                entries.Add(entry);
            }
            else
            {
                _logger.LogWarning("Ignoring allow-list entry '{Entry}': {Error}", raw, error);
            }
        }

        var list = new AllowlistMatcher(version, entries);
        lock (_allowlistGate)
        {
            _allowlist = list;
        }

        _logger.LogInformation("Allow-list version {Version}: {EntryCount} entries", version, entries.Count);
        return list;
    }

    // ---------- plumbing ----------

    private SessionRun? CurrentRun
    {
        get
        {
            lock (_gate)
            {
                return _run;
            }
        }
    }

    /// <summary>
    /// The inbound session steps are strictly sequential: <c>session.active</c> may arrive while <c>ConnectAsync</c> is still
    /// running, and the browser must not be launched before the proxy exists. Ending never queues here — it cancels instead.
    /// </summary>
    private void Enqueue(Func<SessionRun, Task> step, Guid sessionId)
    {
        lock (_stepGate)
        {
            _steps = _steps.ContinueWith(
                async _ =>
                {
                    var run = CurrentRun;
                    if (run is null || run.SessionId != sessionId || run.Cts.IsCancellationRequested)
                    {
                        return;
                    }

                    try
                    {
                        await step(run).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "A session step failed for {SessionId}", sessionId);
                    }
                },
                CancellationToken.None,
                TaskContinuationOptions.None,
                TaskScheduler.Default).Unwrap();
        }
    }

    private static IReadOnlyList<CandidateDto> ToWire(IReadOnlyList<CandidateEndpoint> candidates)
    {
        var list = new List<CandidateDto>(candidates.Count);
        foreach (var candidate in candidates)
        {
            list.Add(new CandidateDto(CandidateTypeNames.ToWire(candidate.Type), candidate.Ip, candidate.Port));
        }

        return list;
    }

    /// <summary>The 32 bytes of <c>secret_b64</c>. Never logged, never stored anywhere but the tunnel's <see cref="SessionMaterial"/>.</summary>
    private static byte[] DecodeSecret(string secretB64)
    {
        var bytes = Convert.FromBase64String(secretB64);
        if (bytes.Length != 32)
        {
            CryptographicOperations.ZeroMemory(bytes);
            throw new ArgumentException("the session secret must be exactly 32 bytes", nameof(secretB64));
        }

        return bytes;
    }

    private DateTimeOffset ServerNow() => _time.GetUtcNow() + _serverClockOffset;

    /// <summary>What is left of the 30 s the server gives a session to become active (docs/ws-protocol.md section 5).</summary>
    private TimeSpan RemainingConnectBudget(SessionRun run)
    {
        var spent = _time.GetElapsedTime(run.CreatedTimestamp);
        var left = _options.ConnectTimeout - spent;
        return left < TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1) : left;
    }

    private async Task SafeAsync(string what, Func<Task> action)
    {
        try
        {
            await action().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Session cleanup step '{Step}' failed", what);
        }
    }

    private void OnTrackerPhaseChanged(SessionPhase phase)
    {
        var session = _tracker.CurrentSession;
        var reason = _tracker.LastEndReason;
        Publish(() =>
        {
            Phase = phase;
            CurrentSession = session;
            LastEndReason = reason;
        });
        _logger.LogInformation("Session phase → {Phase}", phase);
    }

    private void Publish(Action action)
    {
        try
        {
            _options.Post(action);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Publishing a session property change failed");
        }
    }

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        _channel.MessageReceived -= OnMessage;
        _channel.StateChanged -= OnChannelStateChanged;
        _tracker.PhaseChanged -= OnTrackerPhaseChanged;

        var run = CurrentRun;
        if (run is not null)
        {
            // Best effort: the app's shutdown path awaits ShutdownAsync; this only catches a container teardown without it.
            try
            {
                EndCoreAsync(SessionEndReasonNames.LocalEnd(run.Role), notifyServer: false).Wait(_options.CleanupTimeout);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Disposing the coordinator with a live session failed");
            }
        }
    }

    /// <summary>Everything that belongs to one session between <c>session.created</c> and the end of the cleanup.</summary>
    private sealed class SessionRun : IDisposable
    {
        public SessionRun(Guid sessionId, TunnelRole role, int allowlistVersion, DateTimeOffset expiresAt, long createdTimestamp)
        {
            SessionId = sessionId;
            Role = role;
            AllowlistVersion = allowlistVersion;
            ExpiresAt = expiresAt;
            CreatedTimestamp = createdTimestamp;
            CountdownAnchor = createdTimestamp;
            LastStats = createdTimestamp;
        }

        public Guid SessionId { get; }

        public TunnelRole Role { get; }

        public bool IsHost => Role == TunnelRole.Host;

        public int AllowlistVersion { get; }

        public DateTimeOffset ExpiresAt { get; set; }

        /// <summary>Monotonic stamp of <c>session.created</c>: the connect budget counts from here.</summary>
        public long CreatedTimestamp { get; }

        /// <summary>Monotonic stamp the countdown counts from (reset at <c>session.active</c>).</summary>
        public long CountdownAnchor { get; set; }

        public TimeSpan RemainingAtStart { get; set; }

        public long LastStats { get; set; }

        public ITunnelSession? Tunnel { get; set; }

        public ITimer? Ticker { get; set; }

        private ITimer? _connectDeadline;

        /// <summary>The 30 s connect budget counted from <c>session.created</c>; disarmed at <c>session.active</c>.</summary>
        public ITimer? ConnectDeadline
        {
            get => Volatile.Read(ref _connectDeadline);
            set => Volatile.Write(ref _connectDeadline, value);
        }

        /// <summary>True once <c>session.peer_endpoint</c> arrived: from then on <c>ConnectAsync</c> owns the timeout.</summary>
        public bool PeerEndpointSeen { get; set; }

        public bool Connected { get; set; }

        public CancellationTokenSource Cts { get; } = new();

        public TaskCompletionSource ProbeSeen { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public object EndGate { get; } = new();

        public Task? EndTask { get; set; }

        public void OnProbeSeen() => ProbeSeen.TrySetResult();

        public void StopConnectDeadline()
        {
            var deadline = Interlocked.Exchange(ref _connectDeadline, null);
            deadline?.Dispose();
        }

        public void Dispose()
        {
            var ticker = Ticker;
            Ticker = null;
            ticker?.Dispose();
            StopConnectDeadline();
            ProbeSeen.TrySetCanceled();
            Cts.Dispose();
        }
    }
}
