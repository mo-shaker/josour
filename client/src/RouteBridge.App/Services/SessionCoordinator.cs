using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using RouteBridge.Core.Control;
using RouteBridge.Core.Session;
using RouteBridge.Infrastructure.Control;
using RouteBridge.Infrastructure.Session;

namespace RouteBridge.App.Services;

/// <summary>
/// Owns the client-side session lifecycle: listens to <see cref="IControlChannel.MessageReceived"/>, maps frames to
/// <see cref="SessionPhase"/> through <see cref="SessionPhaseTracker"/> (Idle → RequestPending → Preparing → Connecting → Active → Ending → Ended)
/// and exposes <see cref="Phase"/> / <see cref="CurrentSession"/> to the ViewModels on the UI thread.
/// The tunnel, proxy, egress and browser calls are WEEK 4 (marked below); this week only the protocol side is wired.
/// </summary>
public sealed partial class SessionCoordinator : ObservableObject, IDisposable
{
    private static readonly TimeSpan RequestReplyTimeout = TimeSpan.FromSeconds(10);

    private readonly IControlChannel _channel;
    private readonly SessionPhaseTracker _tracker = new();
    private readonly ILogger<SessionCoordinator> _logger;
    private SessionCreatedMessage? _created; // holds secret_b64 for the tunnel (WEEK 4); never logged

    [ObservableProperty]
    private SessionPhase _phase = SessionPhase.Idle;

    [ObservableProperty]
    private SessionInfo? _currentSession;

    [ObservableProperty]
    private string? _lastEndReason;

    public SessionCoordinator(IControlChannel channel, ILogger<SessionCoordinator> logger)
    {
        _channel = channel;
        _logger = logger;
        _tracker.PhaseChanged += OnTrackerPhaseChanged;
        _channel.MessageReceived += OnMessage;
    }

    /// <summary>The pending request id while <see cref="SessionPhase.RequestPending"/>.</summary>
    public Guid? PendingRequestId => _tracker.PendingRequestId;

    // ---------- guest ----------

    /// <summary>Guest: <c>request.create</c>. Returns false when the server answered <c>error</c> (reason in <see cref="LastEndReason"/>) or the channel is unusable.</summary>
    public async Task<bool> RequestSessionAsync(Guid hostDeviceId, int durationMin, CancellationToken ct)
    {
        var requestRef = ControlRef.Next();
        if (!_tracker.TrackRequest(requestRef))
        {
            _logger.LogWarning("request.create ignored: phase is {Phase}", _tracker.Phase);
            return false;
        }

        try
        {
            var reply = await _channel.RequestAsync(new RequestCreateMessage(requestRef, hostDeviceId, durationMin), RequestReplyTimeout, ct);
            if (reply is ErrorMessage error)
            {
                _logger.LogWarning("request.create rejected: {Code} {Message}", error.Code, error.Message); // tracker already returned to Idle via OnMessage
                return false;
            }

            _logger.LogInformation("request.create accepted by the server: request {RequestId}", (reply as RequestCreatedMessage)?.RequestId);
            return true;
        }
        catch (Exception ex) when (ex is TimeoutException or InvalidOperationException)
        {
            _logger.LogWarning(ex, "request.create failed");
            _tracker.Reset();
            return false;
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

    // ---------- host ----------

    public Task AcceptRequestAsync(Guid requestId, CancellationToken ct) =>
        _channel.SendAsync(new RequestAcceptMessage(ControlRef.Next(), requestId), ct);

    public Task RejectRequestAsync(Guid requestId, CancellationToken ct) =>
        _channel.SendAsync(new RequestRejectMessage(ControlRef.Next(), requestId), ct);

    // ---------- both ----------

    /// <summary>User pressed End (or the app is closing): <c>session.end</c> with the role's reason; the server answers <c>session.terminate</c>.</summary>
    public async Task EndSessionAsync(CancellationToken ct)
    {
        var session = _tracker.CurrentSession;
        if (session is null || !_tracker.BeginEnding())
        {
            return;
        }

        var reason = session.IsHost ? "host_ended" : "guest_ended";
        // WEEK 4: bytes_up / bytes_down from ByteCounter and domains from DomainCollector (host only, when log_domains is on).
        await _channel.SendAsync(new SessionEndMessage(session.SessionId, reason, BytesUp: 0, BytesDown: 0, Array.Empty<string>()), ct);
    }

    /// <summary>The UI dismissed the end-of-session summary: back to Idle.</summary>
    public void Acknowledge() => _tracker.Reset();

    // ---------- inbound ----------

    private void OnMessage(ControlMessage message)
    {
        var changed = _tracker.Apply(message);

        switch (message)
        {
            case SessionCreatedMessage m when _tracker.CurrentSession?.SessionId == m.SessionId:
                _created = m;
                _logger.LogInformation(
                    "session.created {SessionId}: role={Role} peer={PeerName}/{PeerDevice} peer_public_ip={PeerIp} same_public_ip={SameIp} expires_at={ExpiresAt:O} allowlist_version={Allowlist}",
                    m.SessionId, m.Role, m.Peer.UserDisplayName, m.Peer.DeviceName, m.PeerPublicIp, m.SamePublicIp, m.ExpiresAt, m.AllowlistVersion);
                // WEEK 4: ITunnelSession.PrepareAsync(secret_b64, role) → session certificate + TunnelListener + CandidateGatherer,
                //         then _channel.SendAsync(new SessionEndpointMessage(m.SessionId, certFpSha256, candidates)).
                break;

            case SessionPeerEndpointMessage m when changed:
                _logger.LogInformation("session.peer_endpoint {SessionId}: {CandidateCount} candidates", m.SessionId, m.Candidates.Count);
                // WEEK 4: tunnel.ConnectAsync(peer candidates, pinned cert_fp_sha256) — symmetric dial (protocol.md 7.1);
                //         host: _channel.SendAsync(new SessionConnectedMessage(id, winner_type, connect_ms, tls_version));
                //         either side on failure: SessionConnectFailedMessage with the per-candidate diagnostics.
                break;

            case SessionActiveMessage m when changed:
                _logger.LogInformation("session.active {SessionId}: expires_at={ExpiresAt:O}", m.SessionId, m.ExpiresAt);
                // WEEK 4: guest → ConnectProxyServer on 127.0.0.1 + BrowserLauncher (check page); host → EgressPolicy/OpenHandler + session.stats every 30 s.
                //         Both: monotonic countdown from expires_at relative to hello.ack server_time (plan 8.5).
                break;

            case SessionTerminateMessage m when _tracker.Phase == SessionPhase.Ending:
                _logger.LogInformation("session.terminate {SessionId}: reason={Reason}", m.SessionId, m.Reason);
                _ = FinishAsync();
                break;

            case RequestResultMessage m when !m.Accepted:
                _logger.LogInformation("request.result {RequestId}: rejected ({Reason})", m.RequestId, m.Reason);
                break;

            case ErrorMessage m when m.Ref is null:
                _logger.LogWarning("Server error without ref: {Code} {Message}", m.Code, m.Message);
                break;
        }
    }

    private async Task FinishAsync()
    {
        try
        {
            // WEEK 4: cleanup order (protocol.md 7.6): close the work browser → stop the proxy/egress → close the tunnel → drop the session keys.
            await Task.CompletedTask;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Session cleanup failed");
        }
        finally
        {
            _created = null;
            _tracker.MarkEnded();
        }
    }

    private void OnTrackerPhaseChanged(SessionPhase phase)
    {
        var session = _tracker.CurrentSession;
        var reason = _tracker.LastEndReason;
        UiThread.Post(() =>
        {
            Phase = phase;
            CurrentSession = session;
            LastEndReason = reason;
        });
        _logger.LogInformation("Session phase → {Phase}", phase);
    }

    public void Dispose()
    {
        _channel.MessageReceived -= OnMessage;
        _tracker.PhaseChanged -= OnTrackerPhaseChanged;
    }
}
