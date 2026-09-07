using System.Net.Sockets;
using System.Text;
using Josour.Core.Tunnel;
using Josour.Tunnel.Transport;

namespace Josour.Tunnel.Tests.Transport;

/// <summary>
/// جانب العميل من الـ Relay مقابل Relay وهمي داخل العملية: المقدمة، الرد، الاقتران، ومرور البايتات المعتمة.
/// </summary>
public class RelayTransportTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    private static RelayTransport Create(FakeRelay relay, Guid sessionId, TunnelRole role, string token = "token-1")
        => new(new RelayTransportOptions
        {
            SessionId = sessionId,
            Role = role,
            TokenProvider = _ => ValueTask.FromResult(token),
            Endpoint = relay.Endpoint,
        });

    [Fact]
    public async Task Name_IsRelay_AndConfiguredEndpointWinsOverCandidate()
    {
        await using var relay = new FakeRelay();
        var transport = Create(relay, Guid.NewGuid(), TunnelRole.Guest);
        Assert.Equal("relay", transport.Name);
        Assert.Equal(relay.Endpoint, transport.Target(new CandidateEndpoint(CandidateType.Lan, "10.1.2.3", 9)));

        var noEndpoint = new RelayTransport(new RelayTransportOptions
        {
            SessionId = Guid.NewGuid(),
            Role = TunnelRole.Host,
            TokenProvider = _ => ValueTask.FromResult("t"),
        });
        var candidate = new CandidateEndpoint(CandidateType.Public, "203.0.113.9", 443);
        Assert.Equal(candidate, noEndpoint.Target(candidate));
    }

    [Fact]
    public async Task Pairs_TwoPeers_AndCarriesOpaqueBytesBothWays()
    {
        await using var relay = new FakeRelay();
        var sessionId = Guid.NewGuid();
        var guest = Create(relay, sessionId, TunnelRole.Guest, "signed-token");
        var host = Create(relay, sessionId, TunnelRole.Host, "signed-token");

        var guestTask = guest.ConnectAsync(relay.Endpoint, Timeout, CancellationToken.None);
        var hostTask = host.ConnectAsync(relay.Endpoint, Timeout, CancellationToken.None);
        await using var guestStream = await guestTask.WaitAsync(Timeout);
        await using var hostStream = await hostTask.WaitAsync(Timeout);

        var up = Encoding.ASCII.GetBytes("guest->host");
        await guestStream.WriteAsync(up);
        await guestStream.FlushAsync();
        var buffer = new byte[up.Length];
        await ReadExactlyAsync(hostStream, buffer);
        Assert.Equal("guest->host", Encoding.ASCII.GetString(buffer));

        var down = Encoding.ASCII.GetBytes("host->guest");
        await hostStream.WriteAsync(down);
        await hostStream.FlushAsync();
        var back = new byte[down.Length];
        await ReadExactlyAsync(guestStream, back);
        Assert.Equal("host->guest", Encoding.ASCII.GetString(back));

        Assert.Equal(2, relay.Received.Count);
        Assert.All(relay.Received, p =>
        {
            Assert.Equal(sessionId, p.SessionId);
            Assert.Equal("signed-token", p.Token);
        });
        Assert.Contains(relay.Received, p => p.Role == TunnelRole.Guest);
        Assert.Contains(relay.Received, p => p.Role == TunnelRole.Host);
    }

    [Fact]
    public async Task Rejected_ThrowsRelayRejected_WithStatus_AndClosesSocket()
    {
        await using var relay = new FakeRelay { ForcedStatus = RelayStatus.NoPeer };
        var transport = Create(relay, Guid.NewGuid(), TunnelRole.Guest);

        var error = await Assert.ThrowsAsync<RelayRejectedException>(
            () => transport.ConnectAsync(relay.Endpoint, Timeout, CancellationToken.None));
        Assert.Equal(RelayStatus.NoPeer, error.Status);
        Assert.Contains("no_peer", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BadToken_IsRejectedAsUnauthorized()
    {
        await using var relay = new FakeRelay { ExpectedToken = "the-right-one" };
        var transport = Create(relay, Guid.NewGuid(), TunnelRole.Host, "the-wrong-one");

        var error = await Assert.ThrowsAsync<RelayRejectedException>(
            () => transport.ConnectAsync(relay.Endpoint, Timeout, CancellationToken.None));
        Assert.Equal(RelayStatus.Unauthorized, error.Status);
    }

    [Fact]
    public async Task SameRoleTwice_IsBusy()
    {
        await using var relay = new FakeRelay();
        var sessionId = Guid.NewGuid();
        var first = Create(relay, sessionId, TunnelRole.Guest);
        var second = Create(relay, sessionId, TunnelRole.Guest);

        var firstTask = first.ConnectAsync(relay.Endpoint, Timeout, CancellationToken.None);
        // ننتظر وصول الأول فعلًا حتى لا يتبادل الاثنان الدورين تحت الحمل
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (relay.Received.Count == 0 && DateTime.UtcNow < deadline) await Task.Delay(20);
        Assert.Single(relay.Received);

        var error = await Assert.ThrowsAsync<RelayRejectedException>(
            () => second.ConnectAsync(relay.Endpoint, Timeout, CancellationToken.None));
        Assert.Equal(RelayStatus.Busy, error.Status);
        Assert.False(firstTask.IsCompleted); // الأول ما زال ينتظر طرفًا مقابلًا
    }

    [Fact]
    public async Task TokenProvider_IsCalledPerConnect()
    {
        await using var relay = new FakeRelay { ForcedStatus = RelayStatus.Internal };
        var calls = 0;
        var transport = new RelayTransport(new RelayTransportOptions
        {
            SessionId = Guid.NewGuid(),
            Role = TunnelRole.Guest,
            TokenProvider = _ => { Interlocked.Increment(ref calls); return ValueTask.FromResult("t" + calls); },
            Endpoint = relay.Endpoint,
        });

        await Assert.ThrowsAsync<RelayRejectedException>(() => transport.ConnectAsync(relay.Endpoint, Timeout, CancellationToken.None));
        await Assert.ThrowsAsync<RelayRejectedException>(() => transport.ConnectAsync(relay.Endpoint, Timeout, CancellationToken.None));
        Assert.Equal(2, calls);
        Assert.Equal(new[] { "t1", "t2" }, relay.Received.Select(p => p.Token));
    }

    [Fact]
    public async Task UnreachableRelay_Fails_LikeAnyTransport()
    {
        var dead = new CandidateEndpoint(CandidateType.Public, "127.0.0.1", Loopback.ClosedPort());
        var transport = new RelayTransport(new RelayTransportOptions
        {
            SessionId = Guid.NewGuid(),
            Role = TunnelRole.Guest,
            TokenProvider = _ => ValueTask.FromResult("t"),
        });
        await Assert.ThrowsAnyAsync<Exception>(() => transport.ConnectAsync(dead, TimeSpan.FromSeconds(2), CancellationToken.None));
    }

    [Fact]
    public async Task NonRelaySpeakingServer_TimesOutWaitingForTheReply()
    {
        using var listener = new TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        var accept = listener.AcceptSocketAsync();
        var transport = new RelayTransport(new RelayTransportOptions
        {
            SessionId = Guid.NewGuid(),
            Role = TunnelRole.Guest,
            TokenProvider = _ => ValueTask.FromResult("t"),
            HandshakeTimeout = TimeSpan.FromMilliseconds(300),
        });

        await Assert.ThrowsAsync<TimeoutException>(
            () => transport.ConnectAsync(new CandidateEndpoint(CandidateType.Public, "127.0.0.1", port), Timeout, CancellationToken.None));
        (await accept).Dispose();
        listener.Stop();
    }

    private static async Task ReadExactlyAsync(Stream stream, Memory<byte> buffer)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer[read..]).AsTask().WaitAsync(Timeout);
            if (n == 0) throw new EndOfStreamException();
            read += n;
        }
    }
}
