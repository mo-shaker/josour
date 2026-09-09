using System.Collections.Concurrent;
using System.Net.WebSockets;
using Josour.Core.Control;
using Josour.Infrastructure.Control;
using Josour.Infrastructure.Tests.Support;

namespace Josour.Infrastructure.Tests;

/// <summary>
/// <see cref="ControlChannel"/> against <see cref="FakeControlServer"/> (a real WebSocket on 127.0.0.1) for the contract of
/// docs/ws-protocol.md: hello/hello.ack, ping/pong, <c>ref</c> correlation, reconnect with backoff and the close codes.
/// </summary>
public sealed class ControlChannelTests
{
    private static readonly CancellationToken None = CancellationToken.None;
    private static readonly Guid DeviceId = new("bbbbbbbb-0000-4000-8000-000000000002");
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    // ---------- hello ----------

    [Fact]
    public async Task Connect_SendsHelloOnTheWsPath_AndReturnsTheAck()
    {
        await using var h = await Harness.StartAsync(connect: false);

        var ack = await h.Channel.ConnectAsync("at-1", DeviceId, new Dictionary<string, object?> { ["os_build"] = "22631", ["system_proxy_present"] = false }, None);
        var connection = await h.Server.NextConnectionAsync();
        var hello = await connection.NextAsync<HelloMessage>();

        Assert.Equal("at-1", hello.Token);
        Assert.Equal(DeviceId, hello.DeviceId);
        Assert.Equal("0.2.0-test", hello.AppVersion);
        Assert.NotNull(hello.Diagnostics);
        Assert.Equal("22631", hello.Diagnostics!["os_build"]?.ToString());
        Assert.Equal("203.0.113.7", ack.PublicIp);
        Assert.Equal(120, ack.Settings.MaxSessionMinutes);
        Assert.Equal(ControlChannelState.Connected, h.Channel.State);
        Assert.Equal(new[] { ControlChannelState.Connecting, ControlChannelState.Connected }, h.States);
        Assert.Equal("/ws", Assert.Single(h.Server.RequestPaths));

        // the ack also reaches the subscribers, so the ViewModels can read settings.max_session_minutes after a reconnect
        var published = await h.Messages.NextAsync<HelloAckMessage>();
        Assert.Equal(ack.AllowlistVersion, published.AllowlistVersion);
    }

    [Fact]
    public async Task Connect_WithoutAToken_IsRejectedLocally()
    {
        await using var h = await Harness.StartAsync(connect: false);

        await Assert.ThrowsAsync<ArgumentException>(() => h.Channel.ConnectAsync(string.Empty, DeviceId, null, None));

        Assert.Equal(ControlChannelState.Disconnected, h.Channel.State);
        Assert.Equal(0, h.Server.ConnectionCount);
    }

    [Fact]
    public async Task Connect_WithoutAServerUrl_FailsWithAClearReason()
    {
        await using var h = await Harness.StartAsync(connect: false);
        h.Settings.Current = h.Settings.Current with { ServerUrl = string.Empty };

        var error = await Assert.ThrowsAsync<ControlChannelClosedException>(() => h.Channel.ConnectAsync("at-1", DeviceId, null, None));

        Assert.Equal(ControlCloseReason.Failed, error.Reason);
        Assert.Equal(ControlCloseReason.Failed, Assert.Single(h.Closes).Reason);
        Assert.Equal(ControlChannelState.Disconnected, h.Channel.State);
    }

    [Theory]
    [InlineData("https://josour.example.com/", "wss://josour.example.com/ws")]
    [InlineData("https://josour.example.com:8443/", "wss://josour.example.com:8443/ws")]
    [InlineData("https://example.com/rb/", "wss://example.com/rb/ws")]
    [InlineData("http://localhost:8000/", "ws://localhost:8000/ws")]
    public void BuildWebSocketUri_MapsSchemeAndKeepsThePathPrefix(string baseUrl, string expected)
    {
        Assert.True(Josour.Infrastructure.Api.ServerUrl.TryNormalize(baseUrl, out var baseUri));

        Assert.Equal(expected, ControlChannel.BuildWebSocketUri(baseUri).ToString());
    }

    // ---------- heart-beat ----------

    [Fact]
    public async Task ServerPing_IsAnsweredWithPong()
    {
        await using var h = await Harness.StartAsync();

        await h.Connection.SendAsync(new PingMessage());

        await h.Connection.NextAsync<PongMessage>();
        Assert.Contains(h.Messages.All, m => m is PingMessage);
    }

    [Fact]
    public async Task Client_SendsPing_EveryInterval()
    {
        await using var h = await Harness.StartAsync(o => o with { PingInterval = TimeSpan.FromMilliseconds(60) });

        await h.Connection.NextAsync<PingMessage>();
        await h.Connection.NextAsync<PingMessage>();

        Assert.True(h.Connection.Received.Count(m => m is PingMessage) >= 2);
    }

    [Fact]
    public async Task NoServerTraffic_IsTreatedAsDead_AndReconnects()
    {
        await using var h = await Harness.StartAsync(o => o with
        {
            PingInterval = TimeSpan.FromMilliseconds(40),
            DeadInterval = TimeSpan.FromMilliseconds(120),
        });
        h.Server.AutoPong = false; // the server stops answering: two missed beats must end the connection

        var second = await h.Server.NextConnectionAsync(TimeSpan.FromSeconds(10));

        await second.NextAsync<HelloMessage>();
        Assert.Contains(ControlChannelState.Reconnecting, h.States);
    }

    // ---------- correlation ----------

    [Fact]
    public async Task RequestAsync_CompletesOnTheReplyWithTheSameRef()
    {
        await using var h = await Harness.StartAsync();
        var requestId = Guid.NewGuid();
        var expires = DateTimeOffset.UtcNow.AddSeconds(60);

        var pending = h.Channel.RequestAsync(new RequestCreateMessage("c1", Guid.NewGuid(), 30), Timeout, None);
        var create = await h.Connection.NextAsync<RequestCreateMessage>();
        await h.Connection.SendAsync(new RequestCreatedMessage(create.Ref, requestId, expires));

        var reply = Assert.IsType<RequestCreatedMessage>(await pending);
        Assert.Equal("c1", reply.Ref);
        Assert.Equal(requestId, reply.RequestId);

        // the reply is raised on MessageReceived as well, so the phase tracker sees it
        var seen = await h.Messages.NextAsync<RequestCreatedMessage>();
        Assert.Equal(requestId, seen.RequestId);
    }

    [Fact]
    public async Task RequestAsync_ErrorWithTheSameRef_ThrowsControlErrorException()
    {
        await using var h = await Harness.StartAsync();

        var pending = h.Channel.RequestAsync(new RequestCreateMessage("c2", Guid.NewGuid(), 30), Timeout, None);
        await h.Connection.NextAsync<RequestCreateMessage>();
        await h.Connection.SendAsync(new ErrorMessage("c2", ControlErrorCodes.HostUnavailable, "That host is not available."));

        var error = await Assert.ThrowsAsync<ControlErrorException>(() => pending);
        Assert.Equal(ControlErrorCodes.HostUnavailable, error.Code);
        Assert.Equal("c2", error.CorrelationRef);
        Assert.Equal("That host is not available.", error.Message);
    }

    [Fact]
    public async Task RequestAsync_IgnoresAnErrorForAnotherRef_AndTimesOut()
    {
        await using var h = await Harness.StartAsync();

        var pending = h.Channel.RequestAsync(new RequestCreateMessage("c3", Guid.NewGuid(), 30), TimeSpan.FromMilliseconds(300), None);
        await h.Connection.NextAsync<RequestCreateMessage>();
        await h.Connection.SendAsync(new ErrorMessage("other", ControlErrorCodes.BadRequest, "not yours"));

        await Assert.ThrowsAsync<TimeoutException>(() => pending);
    }

    [Fact]
    public async Task RequestAsync_WithoutRef_IsRejected()
    {
        await using var h = await Harness.StartAsync();

        await Assert.ThrowsAsync<ArgumentException>(() => h.Channel.RequestAsync(new PingMessage(), Timeout, None));
    }

    [Fact]
    public async Task SendAsync_WhileDisconnected_Throws()
    {
        await using var h = await Harness.StartAsync(connect: false);

        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Channel.SendAsync(new HostAvailableMessage(true, null), None));
    }

    // ---------- robustness ----------

    [Fact]
    public async Task UnknownTypeAndMalformedFrames_DoNotKillTheReceiveLoop()
    {
        await using var h = await Harness.StartAsync();

        await h.Connection.SendRawAsync("""{"type":"session.telemetry","version":9}""");
        await h.Connection.SendRawAsync("not json at all");
        await h.Connection.SendRawAsync("""{"no_type":true}""");
        await h.Connection.SendAsync(new AllowlistUpdatedMessage(7));

        var allowlist = await h.Messages.NextAsync<AllowlistUpdatedMessage>();
        Assert.Equal(7, allowlist.Version);
        Assert.Contains(h.Messages.All, m => m is UnknownControlMessage { Type: "session.telemetry" });
        Assert.Equal(ControlChannelState.Connected, h.Channel.State);
    }

    // ---------- reconnect ----------

    [Fact]
    public async Task Disconnect_ReconnectsWithExponentialBackoff()
    {
        await using var h = await Harness.StartAsync();
        var attempt = 0;
        h.Server.OnHello = async (connection, _) =>
        {
            // the first two reconnect attempts meet a restarting server (1012), the third succeeds
            if (Interlocked.Increment(ref attempt) <= 2)
            {
                await connection.CloseAsync(ControlCloseCodes.ServerRestarting, "restarting");
                return;
            }

            await connection.SendAsync(h.Server.Ack);
        };

        h.Connection.Drop();

        await h.Messages.NextAsync<HelloAckMessage>(timeout: TimeSpan.FromSeconds(10));
        Assert.Equal(ControlChannelState.Connected, h.Channel.State);
        Assert.Equal(
            new[] { TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4) },
            h.Backoffs.Take(3));
        Assert.Contains(ControlChannelState.Reconnecting, h.States);
    }

    [Fact]
    public async Task Backoff_CarriesJitterOnTopOfTheStep()
    {
        await using var h = await Harness.StartAsync(o => o with { NextJitter = () => 1.0 });

        h.Connection.Drop();
        await h.Messages.NextAsync<HelloAckMessage>(timeout: TimeSpan.FromSeconds(10));

        // 1 s + 20 % jitter
        Assert.Equal(TimeSpan.FromMilliseconds(1200), h.Backoffs[0]);
    }

    [Fact]
    public async Task HostAvailable_IsReannouncedAfterAReconnect()
    {
        await using var h = await Harness.StartAsync();
        await h.Channel.SendAsync(new HostAvailableMessage(true, 45000), None);
        await h.Connection.NextAsync<HostAvailableMessage>();

        h.Connection.Drop();
        var second = await h.Server.NextConnectionAsync();

        await second.NextAsync<HelloMessage>();
        var announced = await second.NextAsync<HostAvailableMessage>();
        Assert.True(announced.Available);
        Assert.Equal(45000, announced.ListenPort);
    }

    [Fact]
    public async Task HostAvailableFalse_IsNotReannouncedAfterAReconnect()
    {
        await using var h = await Harness.StartAsync();
        await h.Channel.SendAsync(new HostAvailableMessage(true, 45000), None);
        await h.Connection.NextAsync<HostAvailableMessage>();
        await h.Channel.SendAsync(new HostAvailableMessage(false, null), None);
        await h.Connection.NextAsync<HostAvailableMessage>(m => !m.Available);

        h.Connection.Drop();
        var second = await h.Server.NextConnectionAsync();
        await second.NextAsync<HelloMessage>();

        // The barrier ("the client has processed everything up to here") must land on the connection the client actually
        // kept: this harness reconnects with zero backoff, so a loaded machine can leave an extra short-lived connection
        // behind. Sending to a closed one is a no-op, so the frame simply goes to all of them until the client reports it.
        var barrier = h.Messages.NextAsync<AllowlistUpdatedMessage>(timeout: TimeSpan.FromSeconds(10));
        for (var attempt = 0; attempt < 20 && !barrier.IsCompleted; attempt++)
        {
            foreach (var connection in h.Server.Connections)
            {
                await connection.SendAsync(new AllowlistUpdatedMessage(2));
            }

            await Task.Delay(100);
        }

        await barrier;

        // no re-announcement on any connection made after the drop
        Assert.DoesNotContain(
            h.Server.Connections.Where(c => !ReferenceEquals(c, h.Connection)).SelectMany(c => c.Received),
            m => m is HostAvailableMessage);
    }

    [Fact]
    public async Task PendingRequest_FailsWhenTheConnectionDrops()
    {
        await using var h = await Harness.StartAsync();

        var pending = h.Channel.RequestAsync(new RequestCreateMessage("c4", Guid.NewGuid(), 30), TimeSpan.FromSeconds(30), None);
        await h.Connection.NextAsync<RequestCreateMessage>();
        h.Connection.Drop();

        await Assert.ThrowsAsync<InvalidOperationException>(() => pending);
    }

    // ---------- close codes ----------

    [Fact]
    public async Task Close4403_SignsOutAndDoesNotReconnect()
    {
        await using var h = await Harness.StartAsync();

        await h.Connection.CloseAsync(ControlCloseCodes.DeviceRevoked, "device revoked");

        var closed = await h.NextCloseAsync();
        Assert.Equal(ControlCloseReason.DeviceRevoked, closed.Reason);
        Assert.Equal(ControlCloseCodes.DeviceRevoked, closed.CloseCode);
        Assert.Equal(ControlChannelState.Disconnected, h.Channel.State);

        await Task.Delay(200);
        Assert.Equal(1, h.Server.ConnectionCount);
        Assert.Empty(h.Backoffs);
    }

    [Fact]
    public async Task Close4409_StopsAndSurfacesThatAnotherConnectionTookOver()
    {
        await using var h = await Harness.StartAsync();

        await h.Connection.CloseAsync(ControlCloseCodes.Replaced, "replaced by a newer connection");

        var closed = await h.NextCloseAsync();
        Assert.Equal(ControlCloseReason.ReplacedByAnotherConnection, closed.Reason);
        Assert.Equal(ControlCloseCodes.Replaced, closed.CloseCode);
        Assert.Equal("replaced by a newer connection", closed.Description);

        await Task.Delay(200);
        Assert.Equal(1, h.Server.ConnectionCount);
    }

    [Fact]
    public async Task Close4401_RefreshesTheTokenOnceAndRetries()
    {
        await using var h = await Harness.StartAsync(connect: false);
        var attempt = 0;
        h.Server.OnHello = async (connection, _) =>
        {
            if (Interlocked.Increment(ref attempt) == 1)
            {
                await connection.CloseAsync(ControlCloseCodes.Unauthorized, "token expired");
                return;
            }

            await connection.SendAsync(h.Server.Ack);
        };

        var ack = await h.Channel.ConnectAsync("at-1", DeviceId, null, None);

        Assert.Equal("203.0.113.7", ack.PublicIp);
        Assert.Equal(1, h.Tokens.RefreshCount);
        Assert.Equal("at-1", Assert.Single(h.Tokens.RejectedTokens));
        Assert.Equal(2, h.Server.ConnectionCount);
        var second = h.Server.RequestPaths.Count; // both attempts hit /ws
        Assert.Equal(2, second);
    }

    [Fact]
    public async Task Close4401_Twice_StopsWithUnauthorized()
    {
        await using var h = await Harness.StartAsync(connect: false);
        h.Server.OnHello = (connection, _) => connection.CloseAsync(ControlCloseCodes.Unauthorized, "nope");

        var error = await Assert.ThrowsAsync<ControlChannelClosedException>(() => h.Channel.ConnectAsync("at-1", DeviceId, null, None));

        Assert.Equal(ControlCloseReason.Unauthorized, error.Reason);
        Assert.Equal(ControlCloseCodes.Unauthorized, error.CloseCode);
        Assert.Equal(1, h.Tokens.RefreshCount);
        Assert.Equal(ControlCloseReason.Unauthorized, Assert.Single(h.Closes).Reason);
        Assert.Equal(ControlChannelState.Disconnected, h.Channel.State);
    }

    [Fact]
    public async Task Close1012_ReconnectsInsteadOfStopping()
    {
        await using var h = await Harness.StartAsync();

        await h.Connection.CloseAsync(ControlCloseCodes.ServerRestarting, "server restart");
        var second = await h.Server.NextConnectionAsync();

        await second.NextAsync<HelloMessage>();
        Assert.Empty(h.Closes);
        Assert.Equal(TimeSpan.FromSeconds(1), h.Backoffs[0]);
    }

    [Fact]
    public async Task Close4401_MidSession_RefreshesTheTokenOnceAndComesBackConnected()
    {
        // plan 8.5 "انتهاء access token": the token expires while the channel is up, so the server closes with 4401 on a
        // live connection rather than during the first handshake. One refresh, one reconnect, and the new token on the wire.
        await using var h = await Harness.StartAsync();
        var attempt = 0;
        h.Server.OnHello = async (connection, hello) =>
        {
            if (Interlocked.Increment(ref attempt) == 1)
            {
                await connection.CloseAsync(ControlCloseCodes.Unauthorized, "token expired");
                return;
            }

            await connection.SendAsync(h.Server.Ack);
        };

        await h.Connection.CloseAsync(ControlCloseCodes.Unauthorized, "token expired mid-session");

        await h.Messages.NextAsync<HelloAckMessage>(timeout: TimeSpan.FromSeconds(10));
        Assert.Equal(ControlChannelState.Connected, h.Channel.State);
        Assert.Equal(1, h.Tokens.RefreshCount);
        Assert.Equal("at-1", Assert.Single(h.Tokens.RejectedTokens));

        // The reconnect carried the refreshed token, and the channel never reported a terminal close.
        var hellos = h.Server.Connections.SelectMany(c => c.Received).OfType<HelloMessage>().ToList();
        Assert.Equal("at-2", hellos[^1].Token);
        Assert.Empty(h.Closes);
    }

    [Fact]
    public async Task Close4401_MidSession_ThatARefreshCannotFix_StopsCleanly()
    {
        // The refresh itself failed (the refresh token is gone too): the channel stops instead of looping, and says why,
        // so the app can sign out and the live session is torn down rather than left half-connected.
        await using var h = await Harness.StartAsync();
        h.Tokens.RefreshedToken = null;
        h.Server.OnHello = (connection, _) => connection.CloseAsync(ControlCloseCodes.Unauthorized, "nope");

        await h.Connection.CloseAsync(ControlCloseCodes.Unauthorized, "token expired mid-session");

        var closed = await h.NextCloseAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(ControlCloseReason.Unauthorized, closed.Reason);
        Assert.Equal(ControlChannelState.Disconnected, h.Channel.State);
        Assert.Equal(1, h.Tokens.RefreshCount);
    }

    // ---------- resume / network change (plan 8.5) ----------

    [Fact]
    public async Task ReconnectNow_CutsShortABackoffThatWouldOtherwiseRunForHalfAMinute()
    {
        // The real backoff is used here (no BackoffDelay hook), so the test proves the wait itself is interruptible:
        // without ReconnectNow the second connection would arrive 30 s from now and the test would time out.
        await using var h = await Harness.StartAsync(o => o with
        {
            BackoffSteps = new[] { TimeSpan.FromSeconds(30) },
            BackoffDelay = (delay, ct) => Task.Delay(delay, ct),
        });

        h.Connection.Drop();
        await Task.Delay(100); // let the supervisor notice and enter the backoff

        h.Channel.ReconnectNow("the machine resumed from sleep");

        var second = await h.Server.NextConnectionAsync(TimeSpan.FromSeconds(5));
        await second.NextAsync<HelloMessage>();
        await h.Messages.NextAsync<HelloAckMessage>(timeout: TimeSpan.FromSeconds(5));
        Assert.Equal(ControlChannelState.Connected, h.Channel.State);
    }

    [Fact]
    public async Task ReconnectNow_OnALiveConnection_PingsAtOnceInsteadOfWaitingOutTheInterval()
    {
        // A socket that did not survive the sleep only fails when something is sent on it: the wake makes that happen now.
        await using var h = await Harness.StartAsync(o => o with { PingInterval = TimeSpan.FromMinutes(5) });

        h.Channel.ReconnectNow("a network address changed");

        var ping = await h.Connection.NextAsync<PingMessage>(timeout: TimeSpan.FromSeconds(5));
        Assert.NotNull(ping);
        Assert.Equal(ControlChannelState.Connected, h.Channel.State);
    }

    [Fact]
    public void ReconnectNow_WhileNothingIsRunning_IsHarmless()
    {
        var channel = new ControlChannel(
            new InMemoryAppSettingsStore("https://server.test"),
            new FakeAccessTokenSource(),
            new FakeDeviceInfoProvider(),
            new ControlChannelOptions { UseSystemProxy = false });

        channel.ReconnectNow("the machine resumed from sleep");
        channel.ReconnectNow("a network address changed");

        Assert.Equal(ControlChannelState.Disconnected, channel.State);
    }

    // ---------- shutdown ----------

    [Fact]
    public async Task DisposeAsync_ClosesWithNormalClosure()
    {
        await using var h = await Harness.StartAsync();
        var connection = h.Connection;

        await h.Channel.DisposeAsync();

        await connection.Finished.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(WebSocketCloseStatus.NormalClosure, connection.ClientCloseStatus);
        Assert.Equal(ControlChannelState.Disconnected, h.Channel.State);
        Assert.Empty(h.Backoffs); // a local close is not a reconnect
    }

    [Fact]
    public async Task Connect_AfterDispose_OpensAFreshConnection()
    {
        await using var h = await Harness.StartAsync();

        await h.Channel.DisposeAsync();
        var ack = await h.Channel.ConnectAsync("at-1", DeviceId, null, None);
        var second = await h.Server.NextConnectionAsync();

        await second.NextAsync<HelloMessage>();
        Assert.Equal(120, ack.Settings.MaxSessionMinutes);
        Assert.Equal(ControlChannelState.Connected, h.Channel.State);
    }

    // ---------- harness ----------

    private sealed class Harness : IAsyncDisposable
    {
        private readonly ConcurrentQueue<ControlChannelState> _states = new();
        private readonly ConcurrentQueue<ControlChannelClosed> _closes = new();
        private readonly ConcurrentQueue<TimeSpan> _backoffs = new();

        private Harness(FakeControlServer server, InMemoryAppSettingsStore settings, FakeAccessTokenSource tokens, Func<ControlChannelOptions, ControlChannelOptions>? customize)
        {
            Server = server;
            Settings = settings;
            Tokens = tokens;

            var options = new ControlChannelOptions
            {
                UseSystemProxy = false,
                OpenTimeout = TimeSpan.FromSeconds(5),
                HelloTimeout = TimeSpan.FromSeconds(5),
                PingInterval = TimeSpan.FromMilliseconds(250),
                DeadInterval = TimeSpan.FromSeconds(30),
                NextJitter = () => 0,
                BackoffDelay = (delay, _) =>
                {
                    _backoffs.Enqueue(delay);
                    return Task.CompletedTask;
                },
            };

            Channel = new ControlChannel(settings, tokens, new FakeDeviceInfoProvider(), customize is null ? options : customize(options));
            Channel.StateChanged += _states.Enqueue;
            Channel.Closed += _closes.Enqueue;
            Messages = new MessageCollector(Channel);
        }

        public FakeControlServer Server { get; }

        public InMemoryAppSettingsStore Settings { get; }

        public FakeAccessTokenSource Tokens { get; }

        public ControlChannel Channel { get; }

        public MessageCollector Messages { get; }

        /// <summary>The server side of the first connection (set by <see cref="StartAsync"/> when it connects).</summary>
        public FakeControlConnection Connection { get; private set; } = null!;

        public IReadOnlyList<ControlChannelState> States => _states.ToArray();

        public IReadOnlyList<ControlChannelClosed> Closes => _closes.ToArray();

        public IReadOnlyList<TimeSpan> Backoffs => _backoffs.ToArray();

        public static async Task<Harness> StartAsync(Func<ControlChannelOptions, ControlChannelOptions>? customize = null, bool connect = true)
        {
            var server = new FakeControlServer();
            var harness = new Harness(server, new InMemoryAppSettingsStore(server.ServerUrl), new FakeAccessTokenSource(), customize);
            if (connect)
            {
                await harness.Channel.ConnectAsync("at-1", DeviceId, null, None);
                harness.Connection = await server.NextConnectionAsync();
                await harness.Connection.NextAsync<HelloMessage>();
                await harness.Messages.NextAsync<HelloAckMessage>(); // drained, so a later ack means "reconnected"
            }

            return harness;
        }

        public static Task<Harness> StartAsync(bool connect) => StartAsync(null, connect);

        /// <summary>Waits for the channel to stop for good.</summary>
        public async Task<ControlChannelClosed> NextCloseAsync(TimeSpan? timeout = null)
        {
            var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(5));
            while (DateTime.UtcNow < deadline)
            {
                if (_closes.TryPeek(out var closed))
                {
                    return closed;
                }

                await Task.Delay(20);
            }

            throw new TimeoutException("The channel did not report a terminal close.");
        }

        public async ValueTask DisposeAsync()
        {
            Messages.Dispose();
            await Channel.DisposeAsync();
            await Server.DisposeAsync();
        }
    }
}
