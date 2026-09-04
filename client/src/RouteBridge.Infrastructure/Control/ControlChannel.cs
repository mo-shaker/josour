using System.Buffers;
using System.Collections.Concurrent;
using System.Net.Http;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RouteBridge.Core.Control;
using RouteBridge.Infrastructure.Api;
using RouteBridge.Infrastructure.Device;
using RouteBridge.Infrastructure.Settings;

namespace RouteBridge.Infrastructure.Control;

/// <summary>
/// The real control channel of docs/ws-protocol.md over <see cref="ClientWebSocket"/>: <c>wss://&lt;server&gt;/ws</c> through the
/// system proxy, <c>hello</c> → <c>hello.ack</c>, one receive loop, <c>ping</c>/<c>pong</c> liveness, <c>ref</c> correlation and
/// reconnection with exponential backoff. A drop-in replacement for <c>MockControlChannel</c>.
/// <para>
/// Threading: exactly one receive loop and one send at a time (<see cref="SemaphoreSlim"/>). <see cref="MessageReceived"/> is raised
/// from a separate dispatch loop in arrival order, so a slow handler never stalls the socket; subscribers must still marshal to the UI thread.
/// Replies that carry a <c>ref</c> complete the matching <see cref="RequestAsync"/> AND are raised on <see cref="MessageReceived"/>;
/// an <c>error</c> with that <c>ref</c> fails the call with <see cref="ControlErrorException"/>.
/// </para>
/// <para>
/// Close codes (section 7): <c>4401</c> refreshes the access token once and retries, <c>4403</c> stops with
/// <see cref="ControlCloseReason.DeviceRevoked"/>, <c>4409</c> stops with <see cref="ControlCloseReason.ReplacedByAnotherConnection"/>,
/// everything else (including <c>1012</c>) reconnects with backoff. After a reconnect the last <c>host.available</c> is re-announced.
/// Tokens are never logged.
/// </para>
/// </summary>
public sealed class ControlChannel : IControlChannel
{
    private enum FrameKind { Message, Ignored, Closed }

    private readonly record struct ReceivedFrame(FrameKind Kind, ControlMessage? Message, int? CloseCode, string? CloseDescription)
    {
        public static ReceivedFrame Ignored { get; } = new(FrameKind.Ignored, null, null, null);

        public static ReceivedFrame Of(ControlMessage message) => new(FrameKind.Message, message, null, null);

        public static ReceivedFrame Closed(int? code, string? description) => new(FrameKind.Closed, null, code, description);
    }

    /// <summary>Why the current connection ended; <see cref="CloseCode"/> is set only when the server sent a close frame.</summary>
    private sealed record DisconnectInfo(string Description, int? CloseCode);

    private const string DisconnectedMessage = "The control channel is not connected.";

    private readonly IAppSettingsStore _settings;
    private readonly IAccessTokenSource _tokens;
    private readonly IDeviceInfoProvider _device;
    private readonly ControlChannelOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<ControlChannel> _logger;

    private readonly object _gate = new();
    private readonly SemaphoreSlim _connectGate = new(1, 1);
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<ControlMessage>> _waiters = new(StringComparer.Ordinal);

    private ControlChannelState _state = ControlChannelState.Disconnected;
    private CancellationTokenSource? _lifetime;
    private ClientWebSocket? _socket;
    private Task? _supervisor;
    private Task? _dispatchLoop;
    private Channel<ControlMessage>? _dispatch;

    private Guid _deviceId;
    private Dictionary<string, object?>? _diagnostics;
    private string? _token;
    private HostAvailableMessage? _hostAvailable;
    private long _lastTrafficTicks;
    private volatile bool _disposing;
    private volatile bool _stopped;

    public ControlChannel(
        IAppSettingsStore settings,
        IAccessTokenSource tokens,
        IDeviceInfoProvider device,
        ControlChannelOptions? options = null,
        ILogger<ControlChannel>? logger = null,
        TimeProvider? time = null)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _tokens = tokens ?? throw new ArgumentNullException(nameof(tokens));
        _device = device ?? throw new ArgumentNullException(nameof(device));
        _options = options ?? ControlChannelOptions.Default;
        _logger = logger ?? NullLogger<ControlChannel>.Instance;
        _time = time ?? TimeProvider.System;
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

    public event Action<ControlChannelClosed>? Closed;

    /// <summary>The last <c>hello.ack</c>, refreshed on every (re)connect; null while disconnected.</summary>
    public HelloAckMessage? LastHelloAck { get; private set; }

    /// <summary>The endpoint of the last connect attempt (for the status line and logs); null before the first attempt.</summary>
    public Uri? Endpoint { get; private set; }

    /// <summary><c>https://host/prefix/</c> → <c>wss://host/prefix/ws</c> (<c>http</c> → <c>ws</c> for a loopback developer server).</summary>
    public static Uri BuildWebSocketUri(Uri baseUri, string path = "ws")
    {
        ArgumentNullException.ThrowIfNull(baseUri);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var target = new Uri(baseUri, path);
        var scheme = target.Scheme == Uri.UriSchemeHttps ? "wss" : "ws";
        var port = target.IsDefaultPort ? string.Empty : ":" + target.Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return new Uri(scheme + "://" + target.Host + port + target.PathAndQuery, UriKind.Absolute);
    }

    // ---------- IControlChannel ----------

    public async Task<HelloAckMessage> ConnectAsync(string accessToken, Guid deviceId, Dictionary<string, object?>? diagnostics, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accessToken);

        await _connectGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await TearDownAsync().ConfigureAwait(false);

            var lifetime = new CancellationTokenSource();
            var dispatch = Channel.CreateUnbounded<ControlMessage>(new UnboundedChannelOptions { SingleReader = true });
            lock (_gate)
            {
                _lifetime = lifetime;
                _dispatch = dispatch;
                _deviceId = deviceId;
                _diagnostics = diagnostics;
                _token = accessToken;
                _hostAvailable = null;
                _stopped = false;
                _disposing = false;
            }

            _dispatchLoop = Task.Run(() => DispatchAsync(dispatch.Reader, lifetime.Token), CancellationToken.None);
            SetState(ControlChannelState.Connecting);

            HelloAckMessage ack;
            using (var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, lifetime.Token))
            {
                try
                {
                    ack = await OpenAsync(linked.Token).ConfigureAwait(false);
                }
                catch (ControlChannelClosedException ex)
                {
                    _logger.LogWarning("Control channel could not open: {Reason} ({Message})", ex.Reason, ex.Message);
                    await TearDownAsync().ConfigureAwait(false);
                    SetState(ControlChannelState.Disconnected);
                    RaiseClosed(new ControlChannelClosed(ex.Reason, ex.CloseCode, ex.Message));
                    throw;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning("Control channel could not open: {Message}", ex.Message);
                    await TearDownAsync().ConfigureAwait(false);
                    SetState(ControlChannelState.Disconnected);
                    throw;
                }
                catch (OperationCanceledException)
                {
                    await TearDownAsync().ConfigureAwait(false);
                    SetState(ControlChannelState.Disconnected);
                    throw;
                }
            }

            LastHelloAck = ack;
            SetState(ControlChannelState.Connected);
            _supervisor = Task.Run(() => SuperviseAsync(lifetime.Token), CancellationToken.None);
            return ack;
        }
        finally
        {
            _connectGate.Release();
        }
    }

    public async Task SendAsync(ControlMessage message, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(message);
        ct.ThrowIfCancellationRequested();

        var socket = ConnectedSocket();
        if (message is HostAvailableMessage available)
        {
            // Remembered so the state survives a reconnect (contract section 3: presence.is_available_host).
            lock (_gate)
            {
                _hostAvailable = available;
            }
        }

        await SendFrameAsync(socket, message, ct).ConfigureAwait(false);
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
            var delay = Task.Delay(timeout, _time, timeoutCts.Token);
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
        await _connectGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await TearDownAsync().ConfigureAwait(false);
        }
        finally
        {
            _connectGate.Release();
        }

        SetState(ControlChannelState.Disconnected);
    }

    // ---------- opening ----------

    /// <summary>One full attempt: socket → <c>hello</c> → <c>hello.ack</c>. On <c>4401</c> the token is refreshed once and the attempt repeated.</summary>
    private async Task<HelloAckMessage> OpenAsync(CancellationToken ct)
    {
        var endpoint = ResolveEndpoint();
        Endpoint = endpoint;
        var token = CurrentToken();
        var refreshed = false;

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            ClientWebSocket? socket = null;
            try
            {
                socket = CreateSocket();
                using (var open = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    open.CancelAfter(_options.OpenTimeout);
                    try
                    {
                        await socket.ConnectAsync(endpoint, open.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        throw new TimeoutException($"The WebSocket handshake with {endpoint} did not finish within {_options.OpenTimeout.TotalSeconds:0.#} s.");
                    }
                }

                await SendFrameAsync(socket, new HelloMessage(token, _deviceId, _device.AppVersion, _diagnostics), ct).ConfigureAwait(false);
                var ack = await AwaitHelloAckAsync(socket, ct).ConfigureAwait(false);

                var opened = socket;
                socket = null; // ownership moves to the field
                lock (_gate)
                {
                    _socket = opened;
                    _token = token;
                }

                MarkTraffic();
                LastHelloAck = ack;
                _logger.LogInformation(
                    "Control channel open to {Endpoint}: public_ip={PublicIp} server_time={ServerTime:O} max_session_minutes={MaxMinutes} request_timeout_seconds={RequestTimeout} allowlist_version={AllowlistVersion}",
                    endpoint,
                    ack.PublicIp,
                    ack.ServerTime,
                    ack.Settings.MaxSessionMinutes,
                    ack.Settings.RequestTimeoutSeconds,
                    ack.AllowlistVersion);
                Publish(ack);
                return ack;
            }
            catch (ControlChannelClosedException ex) when (ex.Reason == ControlCloseReason.Unauthorized && !refreshed)
            {
                refreshed = true;
                _logger.LogWarning("The server closed the control channel with {CloseCode}; refreshing the access token once and retrying", ControlCloseCodes.Unauthorized);
                var fresh = await _tokens.RefreshAccessTokenAsync(token, ct).ConfigureAwait(false);
                if (string.IsNullOrEmpty(fresh))
                {
                    throw;
                }

                token = fresh;
            }
            finally
            {
                socket?.Dispose();
            }
        }
    }

    private async Task<HelloAckMessage> AwaitHelloAckAsync(ClientWebSocket socket, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(_options.HelloTimeout);

        while (true)
        {
            ReceivedFrame frame;
            try
            {
                frame = await ReceiveFrameAsync(socket, timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new TimeoutException($"No hello.ack within {_options.HelloTimeout.TotalSeconds:0.#} s.");
            }

            switch (frame.Kind)
            {
                case FrameKind.Closed:
                    throw ClosedDuringHandshake(frame.CloseCode, frame.CloseDescription);
                case FrameKind.Message when frame.Message is HelloAckMessage ack:
                    return ack;
                case FrameKind.Message when frame.Message is PingMessage:
                    await SendFrameAsync(socket, new PongMessage(), ct).ConfigureAwait(false);
                    break;
                case FrameKind.Message:
                    _logger.LogWarning("Ignoring a {Type} frame received before hello.ack", frame.Message!.Type);
                    break;
                default:
                    break;
            }
        }
    }

    private static Exception ClosedDuringHandshake(int? closeCode, string? description) => closeCode switch
    {
        ControlCloseCodes.Unauthorized => new ControlChannelClosedException(
            ControlCloseReason.Unauthorized,
            $"The server rejected hello ({ControlCloseCodes.Unauthorized} {description}).",
            closeCode),
        ControlCloseCodes.DeviceRevoked => new ControlChannelClosedException(
            ControlCloseReason.DeviceRevoked,
            $"This device is no longer allowed to connect ({ControlCloseCodes.DeviceRevoked} {description}).",
            closeCode),
        ControlCloseCodes.Replaced => new ControlChannelClosedException(
            ControlCloseReason.ReplacedByAnotherConnection,
            $"Another connection of this device took over ({ControlCloseCodes.Replaced} {description}).",
            closeCode),
        _ => new WebSocketException(
            WebSocketError.ConnectionClosedPrematurely,
            $"The server closed the control channel with {closeCode?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "no code"} before hello.ack ({description})."),
    };

    private Uri ResolveEndpoint()
    {
        var configured = _settings.Current.ServerUrl;
        if (!ServerUrl.TryNormalize(configured, out var baseUri) || !ServerUrl.IsAllowed(baseUri))
        {
            throw new ControlChannelClosedException(
                ControlCloseReason.Failed,
                "No usable server address is configured; sign in with the server address first.");
        }

        return BuildWebSocketUri(baseUri, _options.Path);
    }

    private string CurrentToken()
    {
        var token = _tokens.AccessToken ?? _token;
        if (string.IsNullOrEmpty(token))
        {
            throw new ControlChannelClosedException(ControlCloseReason.Failed, "No access token: the app is not signed in.");
        }

        return token;
    }

    private ClientWebSocket CreateSocket()
    {
        var socket = new ClientWebSocket();

        // Corporate proxy (plan 8.5). The WebSocket keep-alive is off: the contract's own ping/pong is the liveness signal.
        socket.Options.Proxy = _options.UseSystemProxy ? _options.Proxy ?? HttpClient.DefaultProxy : null;
        socket.Options.KeepAliveInterval = TimeSpan.Zero;
        socket.Options.SetRequestHeader("User-Agent", "RouteBridge/" + _device.AppVersion);
        return socket;
    }

    // ---------- the connection ----------

    private async Task SuperviseAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested && !_disposing)
            {
                var socket = CurrentSocket();
                if (socket is null)
                {
                    break;
                }

                DisconnectInfo info;
                try
                {
                    info = await RunConnectionAsync(socket, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    info = new DisconnectInfo(ex.Message, null);
                }

                DropSocket();
                FailWaiters(new InvalidOperationException(DisconnectedMessage));
                if (ct.IsCancellationRequested || _disposing)
                {
                    break;
                }

                if (TerminalReason(info.CloseCode) is ControlCloseReason terminal)
                {
                    Stop(terminal, info);
                    return;
                }

                _logger.LogWarning(
                    "Control channel lost (close {CloseCode}: {Description}); reconnecting",
                    info.CloseCode?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "none",
                    info.Description);
                SetState(ControlChannelState.Reconnecting);

                if (!await ReconnectAsync(ct).ConfigureAwait(false))
                {
                    return;
                }

                SetState(ControlChannelState.Connected);
                await ReannounceAsync(ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "The control channel supervisor stopped unexpectedly");
        }
        finally
        {
            if (!_stopped && !_disposing)
            {
                SetState(ControlChannelState.Disconnected);
            }
        }
    }

    /// <summary>Receive loop + heart-beat for one socket. Returns why the connection ended; only a cancelled <paramref name="ct"/> throws.</summary>
    private async Task<DisconnectInfo> RunConnectionAsync(ClientWebSocket socket, CancellationToken ct)
    {
        using var connection = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var dead = new StrongBox<bool>(false);
        var heartbeat = Task.Run(() => HeartbeatAsync(socket, connection, dead), CancellationToken.None);
        try
        {
            while (true)
            {
                ReceivedFrame frame;
                try
                {
                    frame = await ReceiveFrameAsync(socket, connection.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (dead.Value)
                {
                    return new DisconnectInfo(
                        $"no frame from the server for {_options.DeadInterval.TotalSeconds:0} s",
                        null);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (OperationCanceledException)
                {
                    return new DisconnectInfo("the connection was cancelled", null);
                }
                catch (Exception ex) when (ex is WebSocketException or IOException or InvalidOperationException or ObjectDisposedException)
                {
                    return new DisconnectInfo(ex.Message, null);
                }

                MarkTraffic();
                switch (frame.Kind)
                {
                    case FrameKind.Closed:
                        return new DisconnectInfo(frame.CloseDescription ?? "closed by the server", frame.CloseCode);
                    case FrameKind.Message:
                        HandleMessage(frame.Message!, socket);
                        break;
                    default:
                        break;
                }
            }
        }
        finally
        {
            connection.Cancel();
            try
            {
                await heartbeat.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "The heart-beat ended with an error");
            }
        }
    }

    private async Task HeartbeatAsync(ClientWebSocket socket, CancellationTokenSource connection, StrongBox<bool> dead)
    {
        var ct = connection.Token;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(_options.PingInterval, _time, ct).ConfigureAwait(false);

                var idle = _time.GetUtcNow() - LastTrafficAt;
                if (idle >= _options.DeadInterval)
                {
                    dead.Value = true;
                    _logger.LogWarning("No frame from the server for {Idle:0.#} s: treating the control channel as dead", idle.TotalSeconds);
                    connection.Cancel();
                    return;
                }

                await SendFrameAsync(socket, new PingMessage(), ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // the connection ended
        }
        catch (Exception ex) when (ex is WebSocketException or IOException or InvalidOperationException or ObjectDisposedException)
        {
            _logger.LogDebug("The heart-beat could not send ping: {Message}", ex.Message);
            connection.Cancel();
        }
    }

    private void HandleMessage(ControlMessage message, ClientWebSocket socket)
    {
        var reference = ControlMessageSerializer.GetRef(message);
        if (reference is not null && _waiters.TryRemove(reference, out var waiter))
        {
            if (message is ErrorMessage error)
            {
                _logger.LogWarning("Server error for ref {Ref}: {Code} {Message}", error.Ref, error.Code, error.Message);
                waiter.TrySetException(ControlErrorException.From(error));
            }
            else
            {
                waiter.TrySetResult(message);
            }
        }

        switch (message)
        {
            case PingMessage:
                QueuePong(socket);
                break;
            case PongMessage:
                break; // liveness only; MarkTraffic already ran
            case UnknownControlMessage unknown:
                _logger.LogInformation("Ignoring an unknown control frame of type {Type}", unknown.Type);
                break;
            default:
                _logger.LogDebug("server → client: {Type}", message.Type);
                break;
        }

        Publish(message);
    }

    /// <summary>Answers a server <c>ping</c> without blocking the receive loop on the send gate.</summary>
    private void QueuePong(ClientWebSocket socket) => _ = Task.Run(
        async () =>
        {
            try
            {
                await SendFrameAsync(socket, new PongMessage(), CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is WebSocketException or IOException or InvalidOperationException or ObjectDisposedException or OperationCanceledException)
            {
                _logger.LogDebug("Could not answer a server ping: {Message}", ex.Message);
            }
        },
        CancellationToken.None);

    // ---------- reconnect ----------

    private async Task<bool> ReconnectAsync(CancellationToken ct)
    {
        var attempt = 0;
        while (!ct.IsCancellationRequested && !_disposing)
        {
            var delay = BackoffFor(attempt);
            _logger.LogInformation("Reconnecting the control channel in {Delay:0.#} s (attempt {Attempt})", delay.TotalSeconds, attempt + 1);
            try
            {
                await _options.BackoffDelay(delay, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return false;
            }

            if (ct.IsCancellationRequested || _disposing)
            {
                return false;
            }

            try
            {
                await OpenAsync(ct).ConfigureAwait(false);
                _logger.LogInformation("Control channel reconnected after {Attempts} attempt(s)", attempt + 1);
                return true;
            }
            catch (ControlChannelClosedException ex)
            {
                Stop(ex.Reason, new DisconnectInfo(ex.Message, ex.CloseCode));
                return false;
            }
            catch (OperationCanceledException)
            {
                return false;
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Reconnect attempt {Attempt} failed: {Message}", attempt + 1, ex.Message);
                attempt++;
            }
        }

        return false;
    }

    /// <summary>1, 2, 4, 8, 16, 30 s (the last step repeats) plus up to <see cref="ControlChannelOptions.JitterFraction"/> on top.</summary>
    private TimeSpan BackoffFor(int attempt)
    {
        var steps = _options.BackoffSteps;
        if (steps.Count == 0)
        {
            return TimeSpan.Zero;
        }

        var step = steps[Math.Min(attempt, steps.Count - 1)];
        var jitter = Math.Clamp(_options.NextJitter(), 0, 1) * _options.JitterFraction;
        return step + step * jitter;
    }

    /// <summary>After a reconnect the server knows nothing about our presence: re-announce <c>host.available</c> when it was on.</summary>
    private async Task ReannounceAsync(CancellationToken ct)
    {
        HostAvailableMessage? available;
        lock (_gate)
        {
            available = _hostAvailable;
        }

        if (available is not { Available: true })
        {
            return;
        }

        try
        {
            var socket = CurrentSocket();
            if (socket is null)
            {
                return;
            }

            await SendFrameAsync(socket, available, ct).ConfigureAwait(false);
            _logger.LogInformation("Re-announced host.available=true (listen_port={ListenPort}) after reconnecting", available.ListenPort);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning("Could not re-announce host.available after reconnecting: {Message}", ex.Message);
        }
    }

    private static ControlCloseReason? TerminalReason(int? closeCode) => closeCode switch
    {
        // 4401 is not terminal here: the next OpenAsync refreshes the token once and only then gives up.
        ControlCloseCodes.DeviceRevoked => ControlCloseReason.DeviceRevoked,
        ControlCloseCodes.Replaced => ControlCloseReason.ReplacedByAnotherConnection,
        _ => null,
    };

    private void Stop(ControlCloseReason reason, DisconnectInfo info)
    {
        _stopped = true;
        _logger.LogWarning(
            "Control channel stopped: {Reason} (close {CloseCode}: {Description})",
            reason,
            info.CloseCode?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "none",
            info.Description);

        FailWaiters(new ControlChannelClosedException(reason, info.Description, info.CloseCode));
        SetState(ControlChannelState.Disconnected);
        RaiseClosed(new ControlChannelClosed(reason, info.CloseCode, info.Description));
    }

    // ---------- frames ----------

    private async Task SendFrameAsync(ClientWebSocket socket, ControlMessage message, CancellationToken ct)
    {
        var payload = ControlMessageSerializer.SerializeToUtf8Bytes(message);
        await _sendGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await socket.SendAsync(new ArraySegment<byte>(payload), WebSocketMessageType.Text, endOfMessage: true, ct).ConfigureAwait(false);
        }
        finally
        {
            _sendGate.Release();
        }

        // Never log the frame itself: hello carries the access token.
        _logger.LogDebug("client → server: {Type} ({Bytes} bytes)", message.Type, payload.Length);
    }

    private async Task<ReceivedFrame> ReceiveFrameAsync(ClientWebSocket socket, CancellationToken ct)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(8192);
        MemoryStream? assembled = null;
        var overflow = false;
        try
        {
            while (true)
            {
                var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), ct).ConfigureAwait(false);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    return ReceivedFrame.Closed((int?)result.CloseStatus, result.CloseStatusDescription);
                }

                if (result.MessageType == WebSocketMessageType.Binary)
                {
                    if (!result.EndOfMessage)
                    {
                        continue;
                    }

                    _logger.LogWarning("Ignoring a binary frame: docs/ws-protocol.md frames are JSON text");
                    return ReceivedFrame.Ignored;
                }

                if (assembled is null && result.EndOfMessage && !overflow)
                {
                    return Parse(buffer, result.Count);
                }

                assembled ??= new MemoryStream(result.Count * 2);
                if (!overflow && assembled.Length + result.Count > _options.MaxFrameBytes)
                {
                    overflow = true;
                    _logger.LogWarning("Dropping a control frame larger than {MaxBytes} bytes", _options.MaxFrameBytes);
                }

                if (!overflow)
                {
                    assembled.Write(buffer, 0, result.Count);
                }

                if (result.EndOfMessage)
                {
                    if (overflow)
                    {
                        return ReceivedFrame.Ignored;
                    }

                    var bytes = assembled.ToArray();
                    return Parse(bytes, bytes.Length);
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
            assembled?.Dispose();
        }
    }

    private ReceivedFrame Parse(byte[] utf8, int count)
    {
        try
        {
            return ReceivedFrame.Of(ControlMessageSerializer.Deserialize(utf8.AsSpan(0, count)));
        }
        catch (JsonException ex)
        {
            // A malformed frame must not kill the loop (contract: the server is the state authority; we resync on the next frame).
            _logger.LogWarning("Dropping an unparseable control frame: {Reason}", ex.Message);
            return ReceivedFrame.Ignored;
        }
    }

    // ---------- plumbing ----------

    private ClientWebSocket ConnectedSocket()
    {
        lock (_gate)
        {
            if (_state != ControlChannelState.Connected || _socket is null || _socket.State != WebSocketState.Open)
            {
                throw new InvalidOperationException(DisconnectedMessage);
            }

            return _socket;
        }
    }

    private ClientWebSocket? CurrentSocket()
    {
        lock (_gate)
        {
            return _socket;
        }
    }

    private void DropSocket()
    {
        ClientWebSocket? socket;
        lock (_gate)
        {
            socket = _socket;
            _socket = null;
        }

        if (socket is null)
        {
            return;
        }

        try
        {
            socket.Abort();
        }
        catch (Exception ex) when (ex is ObjectDisposedException or WebSocketException)
        {
            _logger.LogDebug("Aborting the socket failed: {Message}", ex.Message);
        }

        socket.Dispose();
    }

    private DateTimeOffset LastTrafficAt => new(Volatile.Read(ref _lastTrafficTicks), TimeSpan.Zero);

    private void MarkTraffic() => Volatile.Write(ref _lastTrafficTicks, _time.GetUtcNow().UtcTicks);

    private void Publish(ControlMessage message)
    {
        Channel<ControlMessage>? dispatch;
        lock (_gate)
        {
            dispatch = _dispatch;
        }

        if (dispatch is null || !dispatch.Writer.TryWrite(message))
        {
            _logger.LogDebug("Dropped a {Type} frame: the dispatch loop is gone", message.Type);
        }
    }

    private async Task DispatchAsync(ChannelReader<ControlMessage> reader, CancellationToken ct)
    {
        try
        {
            await foreach (var message in reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                try
                {
                    MessageReceived?.Invoke(message);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "A MessageReceived handler threw for {Type}", message.Type);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // disconnected
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

        _logger.LogInformation("Control channel state → {State}", state);
        try
        {
            StateChanged?.Invoke(state);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "A StateChanged handler threw");
        }
    }

    private void RaiseClosed(ControlChannelClosed closed)
    {
        try
        {
            Closed?.Invoke(closed);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "A Closed handler threw");
        }
    }

    private void FailWaiters(Exception error)
    {
        foreach (var key in _waiters.Keys.ToArray())
        {
            if (_waiters.TryRemove(key, out var waiter))
            {
                waiter.TrySetException(error);
            }
        }
    }

    /// <summary>Closes the current connection (NormalClosure when the socket is still open) and stops the loops. Safe to call twice.</summary>
    private async Task TearDownAsync()
    {
        CancellationTokenSource? lifetime;
        ClientWebSocket? socket;
        Task? supervisor;
        Task? dispatchLoop;
        Channel<ControlMessage>? dispatch;

        lock (_gate)
        {
            lifetime = _lifetime;
            socket = _socket;
            supervisor = _supervisor;
            dispatchLoop = _dispatchLoop;
            dispatch = _dispatch;
            _lifetime = null;
            _socket = null;
            _supervisor = null;
            _dispatchLoop = null;
            _dispatch = null;
        }

        if (lifetime is null && socket is null)
        {
            return;
        }

        _disposing = true;
        try
        {
            if (socket is not null && socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                await CloseCleanlyAsync(socket).ConfigureAwait(false);
            }

            lifetime?.Cancel();
            await WaitQuietlyAsync(supervisor).ConfigureAwait(false);
            await WaitQuietlyAsync(dispatchLoop).ConfigureAwait(false);

            dispatch?.Writer.TryComplete();
            socket?.Dispose();
            lifetime?.Dispose();
            FailWaiters(new InvalidOperationException(DisconnectedMessage));
            LastHelloAck = null;
        }
        finally
        {
            _disposing = false;
            _stopped = false;
        }
    }

    private async Task CloseCleanlyAsync(ClientWebSocket socket)
    {
        using var close = new CancellationTokenSource(_options.CloseTimeout);
        try
        {
            // The send gate keeps this out of the way of a heart-beat ping that may be in flight.
            await _sendGate.WaitAsync(close.Token).ConfigureAwait(false);
            try
            {
                await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "client shutdown", close.Token).ConfigureAwait(false);
                _logger.LogInformation("Control channel closed with NormalClosure");
            }
            finally
            {
                _sendGate.Release();
            }
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException or InvalidOperationException)
        {
            _logger.LogDebug("The clean close did not complete ({Message}); dropping the socket", ex.Message);
        }
    }

    private async Task WaitQuietlyAsync(Task? task)
    {
        if (task is null)
        {
            return;
        }

        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // expected on shutdown
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "A control-channel loop ended with an error");
        }
    }
}
