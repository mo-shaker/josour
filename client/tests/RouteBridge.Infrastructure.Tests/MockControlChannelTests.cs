using RouteBridge.Core.Control;
using RouteBridge.Infrastructure.Control.Mock;
using RouteBridge.Infrastructure.Tests.Support;

namespace RouteBridge.Infrastructure.Tests;

public sealed class MockControlChannelTests
{
    private static readonly CancellationToken None = CancellationToken.None;
    private static readonly Guid DeviceId = new("bbbbbbbb-0000-4000-8000-000000000002");
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);
    private const string Fp = "9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08";

    private static MockControlChannel Create(MockControlChannelOptions? options = null) => new(options ?? MockControlChannelOptions.Fast);

    private static async Task<(MockControlChannel Channel, MessageCollector Messages, HelloAckMessage Ack)> ConnectAsync(MockControlChannelOptions? options = null)
    {
        var channel = Create(options);
        var messages = new MessageCollector(channel);
        var ack = await channel.ConnectAsync("access-token", DeviceId, new Dictionary<string, object?> { ["os_build"] = "22631" }, None);
        await messages.NextAsync<HostsMessage>(m => m.Type == "hosts.snapshot");
        return (channel, messages, ack);
    }

    [Fact]
    public async Task Connect_ReturnsHelloAck_ThenSnapshotWithTwoFakeHosts()
    {
        var channel = Create();
        var states = new List<ControlChannelState>();
        channel.StateChanged += states.Add;
        using var messages = new MessageCollector(channel);

        var ack = await channel.ConnectAsync("access-token", DeviceId, null, None);

        Assert.Equal("203.0.113.10", ack.PublicIp);
        Assert.Equal(120, ack.Settings.MaxSessionMinutes);
        Assert.Equal(60, ack.Settings.RequestTimeoutSeconds);
        Assert.Equal(new[] { 80, 443 }, ack.Settings.AllowedPorts);
        Assert.False(ack.Settings.LogDomains);
        Assert.Equal(1, ack.AllowlistVersion);
        Assert.InRange(ack.ServerTime, DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(1));
        Assert.Equal(ControlChannelState.Connected, channel.State);
        Assert.Equal(new[] { ControlChannelState.Connecting, ControlChannelState.Connected }, states);

        var snapshot = await messages.NextAsync<HostsMessage>();
        Assert.Equal("hosts.snapshot", snapshot.Type);
        Assert.Equal(2, snapshot.Hosts.Count);
        Assert.Contains(snapshot.Hosts, h => h.Reachable == true && h.DeviceId == MockControlChannelOptions.ReachableHostId);
        Assert.Contains(snapshot.Hosts, h => h.Reachable == null && h.DeviceId == MockControlChannelOptions.UnknownReachabilityHostId);
        await channel.DisposeAsync();
    }

    [Fact]
    public async Task Connect_WithoutToken_IsRejected()
    {
        var channel = Create();

        await Assert.ThrowsAsync<InvalidOperationException>(() => channel.ConnectAsync(string.Empty, DeviceId, null, None));

        Assert.Equal(ControlChannelState.Disconnected, channel.State);
    }

    [Fact]
    public async Task HostAvailable_EmitsHostsUpdate_WithThisDevice()
    {
        var (channel, messages, _) = await ConnectAsync();
        using (messages)
        {
            await channel.SendAsync(new HostAvailableMessage(true, 45000), None);
            var update = await messages.NextAsync<HostsMessage>(m => m.Type == "hosts.update");
            Assert.Equal(3, update.Hosts.Count);
            var self = Assert.Single(update.Hosts, h => h.DeviceId == DeviceId);
            Assert.True(self.Reachable); // listen_port given → the fake reachability probe "succeeds"

            await channel.SendAsync(new HostAvailableMessage(false, null), None);
            var gone = await messages.NextAsync<HostsMessage>(m => m.Type == "hosts.update" && m.Hosts.Count == 2);
            Assert.DoesNotContain(gone.Hosts, h => h.DeviceId == DeviceId);
        }

        await channel.DisposeAsync();
    }

    [Fact]
    public async Task GuestHappyPath_FollowsTheProtocolInOrder()
    {
        var (channel, messages, _) = await ConnectAsync();
        using (messages)
        {
            var reply = await channel.RequestAsync(new RequestCreateMessage("c1", MockControlChannelOptions.ReachableHostId, 30), Timeout, None);
            var created = Assert.IsType<RequestCreatedMessage>(reply);
            Assert.Equal("c1", created.Ref);
            Assert.InRange(created.ExpiresAt, DateTimeOffset.UtcNow.AddSeconds(50), DateTimeOffset.UtcNow.AddSeconds(70));

            var result = await messages.NextAsync<RequestResultMessage>();
            Assert.Equal(created.RequestId, result.RequestId);
            Assert.True(result.Accepted);
            Assert.Null(result.Reason);
            Assert.NotNull(result.SessionId);

            var session = await messages.NextAsync<SessionCreatedMessage>();
            Assert.Equal(result.SessionId, session.SessionId);
            Assert.Equal("guest", session.Role);
            Assert.Equal(32, Convert.FromBase64String(session.SecretB64).Length);
            Assert.InRange(session.ExpiresAt, DateTimeOffset.UtcNow.AddMinutes(29), DateTimeOffset.UtcNow.AddMinutes(31));
            Assert.Equal(1, session.AllowlistVersion);
            Assert.Equal("203.0.113.10", session.PeerPublicIp);
            Assert.False(session.SamePublicIp);
            Assert.Equal("Omar Hassan", session.Peer.UserDisplayName);
            Assert.Equal("OMAR-DESKTOP", session.Peer.DeviceName);

            await channel.SendAsync(new SessionEndpointMessage(session.SessionId, Fp, new[] { new CandidateDto("lan", "192.168.1.2", 45000) }), None);
            var peer = await messages.NextAsync<SessionPeerEndpointMessage>();
            Assert.Equal(session.SessionId, peer.SessionId);
            Assert.Matches("^[0-9a-f]{64}$", peer.CertFpSha256);
            var candidate = Assert.Single(peer.Candidates);
            Assert.Equal("public", candidate.Type);
            Assert.Equal("203.0.113.10", candidate.Ip);
            Assert.Equal(40000, candidate.Port);

            var active = await messages.NextAsync<SessionActiveMessage>();
            Assert.Equal(session.SessionId, active.SessionId);
            Assert.Equal(session.ExpiresAt, active.ExpiresAt);

            await channel.SendAsync(new SessionEndMessage(session.SessionId, "guest_ended", 10, 20, new[] { "example.com" }), None);
            var terminate = await messages.NextAsync<SessionTerminateMessage>();
            Assert.Equal(session.SessionId, terminate.SessionId);
            Assert.Equal("guest_ended", terminate.Reason);
            Assert.Null(channel.CurrentSessionId);

            // The ref'd reply was also broadcast, and everything arrived in protocol order.
            var order = new ControlMessage[] { created, result, session, peer, active, terminate }.Select(messages.IndexOf).ToArray();
            Assert.All(order, index => Assert.True(index >= 0));
            Assert.Equal(order.OrderBy(i => i), order);
        }

        await channel.DisposeAsync();
    }

    [Fact]
    public async Task HostAcceptPath_FollowsTheProtocolInOrder()
    {
        var (channel, messages, _) = await ConnectAsync();
        using (messages)
        {
            var requestId = channel.SimulateIncomingRequest("Sara Ahmed", "SARA-LAPTOP", 45);
            Assert.NotNull(requestId);

            var incoming = await messages.NextAsync<RequestIncomingMessage>();
            Assert.Equal(requestId, incoming.RequestId);
            Assert.Equal("Sara Ahmed", incoming.GuestName);
            Assert.Equal("SARA-LAPTOP", incoming.GuestDevice);
            Assert.Equal(45, incoming.DurationMin);
            Assert.Equal(1, incoming.AllowlistVersion);
            Assert.InRange(incoming.ExpiresAt, DateTimeOffset.UtcNow.AddSeconds(50), DateTimeOffset.UtcNow.AddSeconds(70));

            await channel.SendAsync(new RequestAcceptMessage("c7", requestId.Value), None);
            var session = await messages.NextAsync<SessionCreatedMessage>();
            Assert.Equal("host", session.Role);
            Assert.Equal("Sara Ahmed", session.Peer.UserDisplayName);
            Assert.Equal("SARA-LAPTOP", session.Peer.DeviceName);
            Assert.InRange(session.ExpiresAt, DateTimeOffset.UtcNow.AddMinutes(44), DateTimeOffset.UtcNow.AddMinutes(46));
            Assert.Equal(session.SessionId, channel.CurrentSessionId);

            await channel.SendAsync(new SessionEndpointMessage(session.SessionId, Fp, new[] { new CandidateDto("upnp", "203.0.113.55", 45000) }), None);
            var peer = await messages.NextAsync<SessionPeerEndpointMessage>();
            Assert.Equal("public", Assert.Single(peer.Candidates).Type);

            // The host must report session.connected itself; the mock does not activate a host session on its own.
            await messages.ExpectNoneAsync<SessionActiveMessage>(TimeSpan.FromMilliseconds(60));

            await channel.SendAsync(new SessionConnectedMessage(session.SessionId, "lan", 42, "1.3"), None);
            var active = await messages.NextAsync<SessionActiveMessage>();
            Assert.Equal(session.SessionId, active.SessionId);

            await channel.SendAsync(new SessionEndMessage(session.SessionId, "host_ended", 0, 0, Array.Empty<string>()), None);
            var terminate = await messages.NextAsync<SessionTerminateMessage>();
            Assert.Equal("host_ended", terminate.Reason);

            var order = new ControlMessage[] { incoming, session, peer, active, terminate }.Select(messages.IndexOf).ToArray();
            Assert.Equal(order.OrderBy(i => i), order);
        }

        await channel.DisposeAsync();
    }

    [Fact]
    public async Task HostReject_ClearsThePendingRequest_AndSendsNothingBack()
    {
        var (channel, messages, _) = await ConnectAsync();
        using (messages)
        {
            var requestId = channel.SimulateIncomingRequest("Sara Ahmed", "SARA-LAPTOP", 30)!.Value;
            await messages.NextAsync<RequestIncomingMessage>();
            Assert.Null(channel.SimulateIncomingRequest("Other", "OTHER-PC", 15)); // one pending request at a time

            await channel.SendAsync(new RequestRejectMessage("c8", requestId), None);

            await messages.ExpectNoneAsync<ControlMessage>(TimeSpan.FromMilliseconds(60));
            Assert.NotNull(channel.SimulateIncomingRequest("Other", "OTHER-PC", 15)); // pending slot is free again
        }

        await channel.DisposeAsync();
    }

    [Fact]
    public async Task RequestAsync_ErrorWithSameRef_CompletesTheCall()
    {
        var (channel, messages, _) = await ConnectAsync();
        using (messages)
        {
            var reply = await channel.RequestAsync(new RequestCreateMessage("c2", Guid.NewGuid(), 30), Timeout, None);

            var error = Assert.IsType<ErrorMessage>(reply);
            Assert.Equal("c2", error.Ref);
            Assert.Equal("host_unavailable", error.Code);
        }

        await channel.DisposeAsync();
    }

    [Fact]
    public async Task RequestAsync_TimesOut_WhenNoReplyCarriesTheRef()
    {
        var (channel, messages, _) = await ConnectAsync();
        using (messages)
        {
            var requestId = channel.SimulateIncomingRequest("Sara Ahmed", "SARA-LAPTOP", 30)!.Value;
            await messages.NextAsync<RequestIncomingMessage>();

            // request.reject has no ref'd success reply in the protocol (only request.result to the guest), so awaiting it times out.
            await Assert.ThrowsAsync<TimeoutException>(() => channel.RequestAsync(new RequestRejectMessage("c9", requestId), TimeSpan.FromMilliseconds(100), None));
        }

        await channel.DisposeAsync();
    }

    [Fact]
    public async Task RequestAsync_WithoutRef_IsRejected()
    {
        var (channel, messages, _) = await ConnectAsync();
        using (messages)
        {
            await Assert.ThrowsAsync<ArgumentException>(() => channel.RequestAsync(new PingMessage(), Timeout, None));
        }

        await channel.DisposeAsync();
    }

    [Fact]
    public async Task Guest_SendingSessionConnected_GetsForbidden()
    {
        var (channel, messages, _) = await ConnectAsync(MockControlChannelOptions.Fast with { AutoActivateGuestSession = false });
        using (messages)
        {
            await channel.RequestAsync(new RequestCreateMessage("c1", MockControlChannelOptions.ReachableHostId, 15), Timeout, None);
            var session = await messages.NextAsync<SessionCreatedMessage>();

            await channel.SendAsync(new SessionConnectedMessage(session.SessionId, "lan", 10, "1.3"), None);

            var error = await messages.NextAsync<ErrorMessage>();
            Assert.Equal("forbidden", error.Code);
            await messages.ExpectNoneAsync<SessionActiveMessage>(TimeSpan.FromMilliseconds(60));
        }

        await channel.DisposeAsync();
    }

    [Fact]
    public async Task SecondRequest_WhileSessionExists_IsRejectedWithSessionExists()
    {
        var (channel, messages, _) = await ConnectAsync();
        using (messages)
        {
            await channel.RequestAsync(new RequestCreateMessage("c1", MockControlChannelOptions.ReachableHostId, 15), Timeout, None);
            await messages.NextAsync<SessionCreatedMessage>();

            var reply = await channel.RequestAsync(new RequestCreateMessage("c2", MockControlChannelOptions.ReachableHostId, 15), Timeout, None);

            Assert.Equal("session_exists", Assert.IsType<ErrorMessage>(reply).Code);
        }

        await channel.DisposeAsync();
    }

    [Fact]
    public async Task RejectedByFakeHost_WhenAutoAcceptIsOff()
    {
        var (channel, messages, _) = await ConnectAsync(MockControlChannelOptions.Fast with { AutoAcceptGuestRequests = false });
        using (messages)
        {
            await channel.RequestAsync(new RequestCreateMessage("c1", MockControlChannelOptions.ReachableHostId, 15), Timeout, None);

            var result = await messages.NextAsync<RequestResultMessage>();
            Assert.False(result.Accepted);
            Assert.Equal("rejected", result.Reason);
            Assert.Null(result.SessionId);
            await messages.ExpectNoneAsync<SessionCreatedMessage>(TimeSpan.FromMilliseconds(60));
        }

        await channel.DisposeAsync();
    }

    [Fact]
    public async Task Cancel_PendingRequest_YieldsCancelledResult()
    {
        var (channel, messages, _) = await ConnectAsync(MockControlChannelOptions.Fast with { RequestDecisionDelay = TimeSpan.FromSeconds(30) });
        using (messages)
        {
            var created = Assert.IsType<RequestCreatedMessage>(await channel.RequestAsync(new RequestCreateMessage("c1", MockControlChannelOptions.ReachableHostId, 15), Timeout, None));

            await channel.SendAsync(new RequestCancelMessage("c2", created.RequestId), None);

            var result = await messages.NextAsync<RequestResultMessage>();
            Assert.False(result.Accepted);
            Assert.Equal("cancelled", result.Reason);
        }

        await channel.DisposeAsync();
    }

    [Fact]
    public async Task ConnectFailed_TerminatesWithConnectFailed()
    {
        var (channel, messages, _) = await ConnectAsync();
        using (messages)
        {
            await channel.RequestAsync(new RequestCreateMessage("c1", MockControlChannelOptions.ReachableHostId, 15), Timeout, None);
            var session = await messages.NextAsync<SessionCreatedMessage>();

            await channel.SendAsync(new SessionConnectFailedMessage(session.SessionId, new Dictionary<string, object?> { ["lan"] = "timeout" }), None);

            Assert.Equal("connect_failed", (await messages.NextAsync<SessionTerminateMessage>()).Reason);
        }

        await channel.DisposeAsync();
    }

    [Fact]
    public async Task Ping_GetsPong()
    {
        var (channel, messages, _) = await ConnectAsync();
        using (messages)
        {
            await channel.SendAsync(new PingMessage(), None);
            await messages.NextAsync<PongMessage>();
        }

        await channel.DisposeAsync();
    }

    [Fact]
    public async Task Dispose_Disconnects_FailsPendingRequests_AndAllowsReconnect()
    {
        var (channel, messages, _) = await ConnectAsync();
        var states = new List<ControlChannelState>();
        channel.StateChanged += states.Add;
        using (messages)
        {
            var requestId = channel.SimulateIncomingRequest("Sara Ahmed", "SARA-LAPTOP", 30)!.Value;
            await messages.NextAsync<RequestIncomingMessage>();
            var pending = channel.RequestAsync(new RequestRejectMessage("c9", requestId), TimeSpan.FromSeconds(30), None);

            await channel.DisposeAsync();

            Assert.Equal(ControlChannelState.Disconnected, channel.State);
            await Assert.ThrowsAsync<InvalidOperationException>(() => pending);
            await Assert.ThrowsAsync<InvalidOperationException>(() => channel.SendAsync(new PingMessage(), None));

            await channel.ConnectAsync("access-token", DeviceId, null, None);
            Assert.Equal(ControlChannelState.Connected, channel.State);
            await messages.NextAsync<HostsMessage>(m => m.Type == "hosts.snapshot");
        }

        Assert.Equal(new[] { ControlChannelState.Disconnected, ControlChannelState.Connecting, ControlChannelState.Connected }, states);
        await channel.DisposeAsync();
    }
}
