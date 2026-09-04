using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RouteBridge.Core.Control;

namespace RouteBridge.Infrastructure.Control.Mock;

/// <summary>
/// In-process stand-in for the server side of docs/ws-protocol.md, used by the app until the real ControlChannel (week 3).
/// It scripts one fake server: <c>hello.ack</c> + <c>hosts.snapshot</c> on connect, <c>hosts.update</c> on <c>host.available</c>,
/// the guest flow (<c>request.create → request.created → request.result → session.created → session.peer_endpoint → session.active → session.terminate</c>),
/// the host flow (<see cref="SimulateIncomingRequest"/> → <c>request.incoming → request.accept → session.created …</c>), request/session expiry timers,
/// <c>ping/pong</c>, and <c>error</c> frames with the same <c>ref</c> for invalid requests.
/// <para>
/// Server frames are delivered in order from one background pump, never on the caller's thread (like a real receive loop), so
/// subscribers must marshal to the UI thread. Replies that carry a <c>ref</c> complete the matching <see cref="RequestAsync"/> AND are
/// raised on <see cref="MessageReceived"/>. <see cref="DisposeAsync"/> disconnects; the instance can connect again.
/// </para>
/// </summary>
public sealed class MockControlChannel : IControlChannel
{
    private sealed record OutgoingRequest(string Ref, Guid RequestId, HostInfoDto Host, int DurationMin, CancellationTokenSource Timer);

    private sealed record IncomingRequest(Guid RequestId, string GuestName, string GuestDevice, int DurationMin, DateTimeOffset ExpiresAt, CancellationTokenSource Timer);

    private sealed class MockSession
    {
        public required Guid Id { get; init; }
        public required string Role { get; init; }
        public required DateTimeOffset ExpiresAt { get; init; }
        public required PeerDto Peer { get; init; }
        public required CancellationTokenSource Timer { get; init; }
        public bool EndpointReceived { get; set; }
        public bool Active { get; set; }
    }

    private readonly MockControlChannelOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<MockControlChannel> _logger;
    private readonly object _gate = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<ControlMessage>> _waiters = new(StringComparer.Ordinal);

    private ControlChannelState _state = ControlChannelState.Disconnected;
    private CancellationTokenSource? _lifetime;
    private Channel<Func<CancellationToken, Task>>? _outbound;
    private Task? _pump;

    private Guid _deviceId;
    private bool _hostAvailable;
    private int? _listenPort;
    private OutgoingRequest? _outgoing;
    private IncomingRequest? _incoming;
    private MockSession? _session;

    public MockControlChannel(MockControlChannelOptions? options = null, TimeProvider? time = null, ILogger<MockControlChannel>? logger = null)
    {
        _options = options ?? MockControlChannelOptions.Default;
        _time = time ?? TimeProvider.System;
        _logger = logger ?? NullLogger<MockControlChannel>.Instance;
    }

    public ControlChannelState State
    {
        get
        {
            lock (_gate)
            {
                return _state;
            }
        }
    }

    public event Action<ControlChannelState>? StateChanged;

    public event Action<ControlMessage>? MessageReceived;

    /// <summary>The session the fake server currently holds for this device, or null.</summary>
    public Guid? CurrentSessionId
    {
        get
        {
            lock (_gate)
            {
                return _session?.Id;
            }
        }
    }

    // ---------- IControlChannel ----------

    public async Task<HelloAckMessage> ConnectAsync(string accessToken, Guid deviceId, Dictionary<string, object?>? diagnostics, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            // The real server closes with 4401 when hello carries no valid token.
            throw new InvalidOperationException("hello rejected: missing access token (4401).");
        }

        await DisconnectCoreAsync().ConfigureAwait(false);
        SetState(ControlChannelState.Connecting);

        try
        {
            await DelayAsync(_options.ConnectDelay, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            SetState(ControlChannelState.Disconnected);
            throw;
        }

        var lifetime = new CancellationTokenSource();
        var outbound = Channel.CreateUnbounded<Func<CancellationToken, Task>>(new UnboundedChannelOptions { SingleReader = true });
        lock (_gate)
        {
            _lifetime = lifetime;
            _outbound = outbound;
            _deviceId = deviceId;
            _hostAvailable = false;
            _listenPort = null;
            _outgoing = null;
            _incoming = null;
            _session = null;
        }

        _pump = Task.Run(() => PumpAsync(outbound.Reader, lifetime.Token), CancellationToken.None);
        SetState(ControlChannelState.Connected);

        _logger.LogInformation("Mock control channel connected for device {DeviceId} ({DiagnosticCount} diagnostics)", deviceId, diagnostics?.Count ?? 0);

        var ack = new HelloAckMessage(_time.GetUtcNow(), _options.PublicIp, _options.Settings, _options.AllowlistVersion);
        Schedule(_options.SnapshotDelay, () => Emit(new HostsMessage("hosts.snapshot", BuildHostList())));
        return ack;
    }

    public Task SendAsync(ControlMessage message, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(message);
        ct.ThrowIfCancellationRequested();
        EnsureConnected();
        _logger.LogDebug("client → mock: {Type}", message.Type);

        switch (message)
        {
            case HostAvailableMessage m:
                OnHostAvailable(m);
                break;
            case RequestCreateMessage m:
                OnRequestCreate(m);
                break;
            case RequestCancelMessage m:
                OnRequestCancel(m);
                break;
            case RequestAcceptMessage m:
                OnRequestAccept(m);
                break;
            case RequestRejectMessage m:
                OnRequestReject(m);
                break;
            case SessionEndpointMessage m:
                OnSessionEndpoint(m);
                break;
            case SessionConnectedMessage m:
                OnSessionConnected(m);
                break;
            case SessionConnectFailedMessage m:
                OnSessionConnectFailed(m);
                break;
            case SessionStatsMessage m:
                _logger.LogDebug("session.stats {SessionId}: up={BytesUp} down={BytesDown}", m.SessionId, m.BytesUp, m.BytesDown);
                break;
            case SessionEndMessage m:
                OnSessionEnd(m);
                break;
            case PingMessage:
                Schedule(TimeSpan.Zero, () => Emit(new PongMessage()));
                break;
            case PongMessage:
                break;
            case HelloMessage:
                ScheduleError(null, "bad_request", "hello is only valid as the first frame.");
                break;
            default:
                ScheduleError(ControlMessageSerializer.GetRef(message), "bad_request", $"Unknown message type '{message.Type}'.");
                break;
        }

        return Task.CompletedTask;
    }

    public async Task<ControlMessage> RequestAsync(ControlMessage message, TimeSpan timeout, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(message);
        var requestRef = ControlMessageSerializer.GetRef(message)
            ?? throw new ArgumentException($"Message type '{message.Type}' carries no ref and cannot be awaited.", nameof(message));

        var waiter = new TaskCompletionSource<ControlMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_waiters.TryAdd(requestRef, waiter))
        {
            throw new InvalidOperationException($"A request with ref '{requestRef}' is already pending.");
        }

        try
        {
            await SendAsync(message, ct).ConfigureAwait(false);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var delay = DelayAsync(timeout, timeoutCts.Token);
            var completed = await Task.WhenAny(waiter.Task, delay).ConfigureAwait(false);
            if (completed == waiter.Task)
            {
                timeoutCts.Cancel();
                return await waiter.Task.ConfigureAwait(false);
            }

            ct.ThrowIfCancellationRequested();
            throw new TimeoutException($"No reply for ref '{requestRef}' within {timeout.TotalSeconds:0.#} s.");
        }
        finally
        {
            _waiters.TryRemove(requestRef, out _);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectCoreAsync().ConfigureAwait(false);
        SetState(ControlChannelState.Disconnected);
    }

    // ---------- host-side simulation ----------

    /// <summary>
    /// Simulates a guest asking this device (the host) for a session: emits <c>request.incoming</c>. Returns the request id,
    /// or null when the fake server would not deliver it (not connected, another request pending, or a session in progress).
    /// </summary>
    public Guid? SimulateIncomingRequest(string guestName, string guestDevice, int durationMin)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(guestName);
        ArgumentException.ThrowIfNullOrWhiteSpace(guestDevice);
        ArgumentOutOfRangeException.ThrowIfLessThan(durationMin, 1);

        Guid requestId;
        DateTimeOffset expiresAt;
        lock (_gate)
        {
            if (_state != ControlChannelState.Connected)
            {
                _logger.LogWarning("SimulateIncomingRequest ignored: not connected");
                return null;
            }

            if (_incoming is not null || _session is not null)
            {
                _logger.LogWarning("SimulateIncomingRequest ignored: a request or session is already in progress");
                return null;
            }

            requestId = Guid.NewGuid();
            expiresAt = _time.GetUtcNow() + _options.RequestTimeout;
            var timer = new CancellationTokenSource();
            _incoming = new IncomingRequest(requestId, guestName, guestDevice, durationMin, expiresAt, timer);
            StartTimer(_options.RequestTimeout, timer.Token, () => ExpireIncoming(requestId));
        }

        Schedule(TimeSpan.Zero, () => Emit(new RequestIncomingMessage(requestId, guestName, guestDevice, durationMin, _options.AllowlistVersion, expiresAt)));
        return requestId;
    }

    // ---------- client → server handlers (called on the sender's thread; only mutate state and schedule) ----------

    private void OnHostAvailable(HostAvailableMessage m)
    {
        lock (_gate)
        {
            _hostAvailable = m.Available;
            _listenPort = m.Available ? m.ListenPort : null;
        }

        _logger.LogInformation("host.available={Available} listen_port={ListenPort}", m.Available, m.ListenPort);
        Schedule(_options.HostsUpdateDelay, () => Emit(new HostsMessage("hosts.update", BuildHostList())));
    }

    private void OnRequestCreate(RequestCreateMessage m)
    {
        Guid requestId;
        DateTimeOffset expiresAt;
        lock (_gate)
        {
            if (_session is not null)
            {
                ScheduleError(m.Ref, "session_exists", "You already have a session in progress.");
                return;
            }

            if (_outgoing is not null || _incoming is not null)
            {
                ScheduleError(m.Ref, "request_pending", "A request is already pending.");
                return;
            }

            if (m.DurationMin < 1 || m.DurationMin > _options.Settings.MaxSessionMinutes)
            {
                ScheduleError(m.Ref, "bad_request", $"duration_min must be 1..{_options.Settings.MaxSessionMinutes}.");
                return;
            }

            var host = _options.FakeHosts.FirstOrDefault(h => h.DeviceId == m.HostDeviceId);
            if (host is null)
            {
                ScheduleError(m.Ref, "host_unavailable", "That host is not available.");
                return;
            }

            requestId = Guid.NewGuid();
            expiresAt = _time.GetUtcNow() + _options.RequestTimeout;
            var timer = new CancellationTokenSource();
            _outgoing = new OutgoingRequest(m.Ref, requestId, host, m.DurationMin, timer);
            StartTimer(_options.RequestDecisionDelay, timer.Token, () => DecideOutgoing(requestId));
            StartTimer(_options.RequestTimeout, timer.Token, () => ExpireOutgoing(requestId));
        }

        Schedule(TimeSpan.Zero, () => Emit(new RequestCreatedMessage(m.Ref, requestId, expiresAt)));
    }

    private void OnRequestCancel(RequestCancelMessage m)
    {
        lock (_gate)
        {
            if (_outgoing is null || _outgoing.RequestId != m.RequestId)
            {
                ScheduleError(m.Ref, "not_found", "No pending request with that id.");
                return;
            }

            _outgoing.Timer.Cancel();
            _outgoing = null;
        }

        Schedule(TimeSpan.Zero, () => Emit(new RequestResultMessage(m.RequestId, Accepted: false, "cancelled", null)));
    }

    private void OnRequestAccept(RequestAcceptMessage m)
    {
        SessionCreatedMessage created;
        lock (_gate)
        {
            if (_incoming is null || _incoming.RequestId != m.RequestId)
            {
                ScheduleError(m.Ref, "not_found", "No incoming request with that id.");
                return;
            }

            var incoming = _incoming;
            incoming.Timer.Cancel();
            _incoming = null;
            created = CreateSession("host", incoming.DurationMin, new PeerDto(incoming.GuestName, incoming.GuestDevice));
        }

        Schedule(TimeSpan.Zero, () => Emit(created));
    }

    private void OnRequestReject(RequestRejectMessage m)
    {
        lock (_gate)
        {
            if (_incoming is null || _incoming.RequestId != m.RequestId)
            {
                ScheduleError(m.Ref, "not_found", "No incoming request with that id.");
                return;
            }

            _incoming.Timer.Cancel();
            _logger.LogInformation(
                "request.reject {RequestId}: request.result {{accepted:false, reason:rejected}} would go to guest {GuestName} (nobody in the mock)",
                m.RequestId,
                _incoming.GuestName);
            _incoming = null;
        }
    }

    private void OnSessionEndpoint(SessionEndpointMessage m)
    {
        bool activateForGuest;
        lock (_gate)
        {
            if (_session is null || _session.Id != m.SessionId)
            {
                ScheduleError(null, "not_found", "No session with that id.");
                return;
            }

            _session.EndpointReceived = true;
            activateForGuest = _session.Role == "guest" && _options.AutoActivateGuestSession;
        }

        var sessionId = m.SessionId;
        var peerEndpoint = new SessionPeerEndpointMessage(
            sessionId,
            _options.PeerCertFingerprint,
            new[] { new CandidateDto("public", _options.PeerPublicIp, _options.PeerCandidatePort) });

        Schedule(_options.PeerEndpointDelay, () => Emit(peerEndpoint));
        if (activateForGuest)
        {
            // The fake host on the other side "connects" and reports session.connected; the server then broadcasts session.active.
            Schedule(_options.ActiveDelay, () => ActivateSession(sessionId));
        }
    }

    private void OnSessionConnected(SessionConnectedMessage m)
    {
        lock (_gate)
        {
            if (_session is null || _session.Id != m.SessionId)
            {
                ScheduleError(null, "not_found", "No session with that id.");
                return;
            }

            if (_session.Role != "host")
            {
                // docs/ws-protocol.md section 5: session.connected is host-only; the guest gets error(forbidden).
                ScheduleError(null, "forbidden", "Only the host reports session.connected.");
                return;
            }
        }

        _logger.LogInformation("session.connected {SessionId}: winner={Winner} connect_ms={ConnectMs} tls={Tls}", m.SessionId, m.WinnerType, m.ConnectMs, m.TlsVersion);
        var sessionId = m.SessionId;
        Schedule(TimeSpan.Zero, () => ActivateSession(sessionId));
    }

    private void OnSessionConnectFailed(SessionConnectFailedMessage m)
    {
        _logger.LogInformation("session.connect_failed {SessionId} ({DiagnosticCount} diagnostics)", m.SessionId, m.Diagnostics?.Count ?? 0);
        var sessionId = m.SessionId;
        Schedule(TimeSpan.Zero, () => TerminateSession(sessionId, "connect_failed"));
    }

    private void OnSessionEnd(SessionEndMessage m)
    {
        lock (_gate)
        {
            if (_session is null || _session.Id != m.SessionId)
            {
                ScheduleError(null, "not_found", "No session with that id.");
                return;
            }
        }

        _logger.LogInformation("session.end {SessionId}: reason={Reason} up={BytesUp} down={BytesDown} domains={DomainCount}", m.SessionId, m.Reason, m.BytesUp, m.BytesDown, m.Domains?.Count ?? 0);
        var sessionId = m.SessionId;
        var reason = m.Reason;
        Schedule(TimeSpan.Zero, () => TerminateSession(sessionId, reason));
    }

    // ---------- fake server actions (run on the pump) ----------

    private void DecideOutgoing(Guid requestId)
    {
        OutgoingRequest request;
        SessionCreatedMessage? created = null;
        lock (_gate)
        {
            if (_outgoing is null || _outgoing.RequestId != requestId)
            {
                return;
            }

            request = _outgoing;
            request.Timer.Cancel();
            _outgoing = null;

            if (_options.AutoAcceptGuestRequests)
            {
                created = CreateSession("guest", request.DurationMin, new PeerDto(request.Host.UserDisplayName, request.Host.DeviceName));
            }
        }

        if (created is null)
        {
            Emit(new RequestResultMessage(requestId, Accepted: false, "rejected", null));
            return;
        }

        Emit(new RequestResultMessage(requestId, Accepted: true, null, created.SessionId));
        Emit(created);
    }

    private void ExpireOutgoing(Guid requestId)
    {
        lock (_gate)
        {
            if (_outgoing is null || _outgoing.RequestId != requestId)
            {
                return;
            }

            _outgoing = null;
        }

        Emit(new RequestResultMessage(requestId, Accepted: false, "expired", null));
    }

    private void ExpireIncoming(Guid requestId)
    {
        lock (_gate)
        {
            if (_incoming is null || _incoming.RequestId != requestId)
            {
                return;
            }

            _incoming = null;
        }

        Emit(new RequestExpiredMessage(requestId));
    }

    private void ActivateSession(Guid sessionId)
    {
        DateTimeOffset expiresAt;
        lock (_gate)
        {
            if (_session is null || _session.Id != sessionId || _session.Active)
            {
                return;
            }

            _session.Active = true;
            expiresAt = _session.ExpiresAt;
        }

        Emit(new SessionActiveMessage(sessionId, expiresAt));
    }

    private void TerminateSession(Guid sessionId, string reason)
    {
        lock (_gate)
        {
            if (_session is null || _session.Id != sessionId)
            {
                return;
            }

            _session.Timer.Cancel();
            _session = null;
        }

        Emit(new SessionTerminateMessage(sessionId, reason));
    }

    /// <summary>Caller holds <c>_gate</c>.</summary>
    private SessionCreatedMessage CreateSession(string role, int durationMin, PeerDto peer)
    {
        var id = Guid.NewGuid();
        var expiresAt = _time.GetUtcNow().AddMinutes(durationMin);
        var timer = new CancellationTokenSource();
        _session = new MockSession { Id = id, Role = role, ExpiresAt = expiresAt, Peer = peer, Timer = timer };
        StartTimer(TimeSpan.FromMinutes(durationMin), timer.Token, () => TerminateSession(id, "expired"));

        return new SessionCreatedMessage(
            id,
            role,
            Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
            expiresAt,
            _options.AllowlistVersion,
            _options.PeerPublicIp,
            _options.SamePublicIp,
            peer);
    }

    private IReadOnlyList<HostInfoDto> BuildHostList()
    {
        var hosts = new List<HostInfoDto>(_options.FakeHosts);
        lock (_gate)
        {
            // A host with a session in progress is not listed (docs/ws-protocol.md section 5 rules).
            if (_hostAvailable && _session is null)
            {
                hosts.Add(new HostInfoDto(_deviceId, _options.SelfUserDisplayName, _options.SelfDeviceName, _listenPort is null ? null : true));
            }
        }

        return hosts;
    }

    // ---------- plumbing ----------

    private void EnsureConnected()
    {
        if (State != ControlChannelState.Connected)
        {
            throw new InvalidOperationException("The control channel is not connected.");
        }
    }

    private void SetState(ControlChannelState state)
    {
        lock (_gate)
        {
            if (_state == state)
            {
                return;
            }

            _state = state;
        }

        try
        {
            StateChanged?.Invoke(state);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "StateChanged handler threw");
        }
    }

    private void ScheduleError(string? requestRef, string code, string message) =>
        Schedule(TimeSpan.Zero, () => Emit(new ErrorMessage(requestRef, code, message)));

    /// <summary>Queues a server action behind everything already queued; the delay is waited on the pump, preserving order.</summary>
    private void Schedule(TimeSpan delay, Action action)
    {
        Channel<Func<CancellationToken, Task>>? outbound;
        lock (_gate)
        {
            outbound = _outbound;
        }

        if (outbound is null || !outbound.Writer.TryWrite(async ct =>
            {
                await DelayAsync(delay, ct).ConfigureAwait(false);
                action();
            }))
        {
            _logger.LogDebug("Dropped a scheduled server action: channel disconnected");
        }
    }

    /// <summary>A timer independent of the pump (request/session expiry); fires <paramref name="action"/> through the pump.</summary>
    private void StartTimer(TimeSpan delay, CancellationToken token, Action action)
    {
        var lifetime = _lifetime?.Token ?? new CancellationToken(canceled: true);
        var linked = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime);
        _ = Task.Run(
            async () =>
            {
                try
                {
                    await DelayAsync(delay, linked.Token).ConfigureAwait(false);
                    Schedule(TimeSpan.Zero, action);
                }
                catch (OperationCanceledException)
                {
                    // timer cancelled: decision made, session ended, or channel disposed
                }
                finally
                {
                    linked.Dispose();
                }
            },
            CancellationToken.None);
    }

    private Task DelayAsync(TimeSpan delay, CancellationToken ct) =>
        delay <= TimeSpan.Zero ? Task.CompletedTask : Task.Delay(delay, _time, ct);

    private async Task PumpAsync(ChannelReader<Func<CancellationToken, Task>> reader, CancellationToken ct)
    {
        try
        {
            await foreach (var action in reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                try
                {
                    await action(ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Mock server action failed");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // disconnected
        }
    }

    private void Emit(ControlMessage message)
    {
        _logger.LogDebug("mock → client: {Type}", message.Type);

        var replyRef = ControlMessageSerializer.GetRef(message);
        if (replyRef is not null && _waiters.TryRemove(replyRef, out var waiter))
        {
            waiter.TrySetResult(message);
        }

        try
        {
            MessageReceived?.Invoke(message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "MessageReceived handler threw for {Type}", message.Type);
        }
    }

    private async Task DisconnectCoreAsync()
    {
        CancellationTokenSource? lifetime;
        Channel<Func<CancellationToken, Task>>? outbound;
        Task? pump;
        lock (_gate)
        {
            lifetime = _lifetime;
            outbound = _outbound;
            pump = _pump;
            _lifetime = null;
            _outbound = null;
            _pump = null;
            _outgoing?.Timer.Cancel();
            _incoming?.Timer.Cancel();
            _session?.Timer.Cancel();
            _outgoing = null;
            _incoming = null;
            _session = null;
            _hostAvailable = false;
            _listenPort = null;
        }

        if (lifetime is null)
        {
            return;
        }

        outbound?.Writer.TryComplete();
        lifetime.Cancel();
        if (pump is not null)
        {
            try
            {
                await pump.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // expected
            }
        }

        lifetime.Dispose();

        foreach (var key in _waiters.Keys.ToArray())
        {
            if (_waiters.TryRemove(key, out var waiter))
            {
                waiter.TrySetException(new InvalidOperationException("The control channel was disconnected."));
            }
        }
    }
}
