using System.Net;
using System.Net.Sockets;
using Josour.Core.Net;
using Josour.Proxy;

namespace Josour.E2E.Tests;

/// <summary>
/// "Can the guest reach, through the tunnel, what the host machine itself is listening on?" — the answer these tests prove: no,
/// and the refusal happens **after** the name is resolved and **before** any socket.
///
/// <para>
/// The threat model: the guest fully controls the destination's name (they write it in CONNECT) and may control a DNS zone, so they can make
/// a name that is in the list resolve to 127.0.0.1 or to the host's internal address. Had it got through, the guest would have become a bridge to the tunnel's
/// listener on the host, to the relay's port, and to everything the host's network is listening on. This is the most dangerous case in the whole product.
/// </para>
///
/// <para>
/// Unlike the rest of the E2E tests, the address policy here is the production one with no loopback exemption: the victim is a real listener on
/// 127.0.0.1 and its accept counter is the proof — a zero means nobody connected to it.
/// </para>
/// </summary>
public class TunnelSelfAccessTests : IAsyncLifetime
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    private Victim _victim = null!;
    private InProcessTunnelPair _pair = null!;

    /// <summary>A listener on the host standing in for the tunnel's listener, the relay's port, or any internal service. It must accept nothing.</summary>
    private sealed class Victim : IDisposable
    {
        private readonly TcpListener _listener;
        private int _accepted;

        public Victim()
        {
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _ = Task.Run(async () =>
            {
                try
                {
                    while (true)
                    {
                        using var socket = await _listener.AcceptSocketAsync();
                        Interlocked.Increment(ref _accepted);
                    }
                }
                catch { /* stopped */ }
            });
        }

        public int Port { get; }
        public int Accepted => Volatile.Read(ref _accepted);
        public void Dispose() => _listener.Stop();
    }

    public async Task InitializeAsync()
    {
        _victim = new Victim();
        // self.test and metadata.test are in the list on both sides: the list is not what protects us here — the address policy is.
        _pair = await InProcessTunnelPair.CreateAsync(
            extraEntries: new[] { $"self.test:{_victim.Port}", $"metadata.test:{_victim.Port}", $"mixed.test:{_victim.Port}" },
            hostAddressBlocker: a => IpRangePolicy.IsBlocked(a),
            guestAddressBlocker: a => IpRangePolicy.IsBlocked(a),
            extraHostNames: new Dictionary<string, string[]>
            {
                ["self.test"] = new[] { "127.0.0.1" },
                ["metadata.test"] = new[] { "169.254.169.254" },
                ["mixed.test"] = new[] { "93.184.216.34", "127.0.0.1" },
            });
    }

    public async Task DisposeAsync()
    {
        await _pair.DisposeAsync();
        _victim.Dispose();
    }

    [Theory]
    [InlineData("self.test")]      // loopback on the host machine: the tunnel's listener and the relay's port live here
    [InlineData("metadata.test")]  // cloud metadata
    [InlineData("mixed.test")]     // a DNS answer mixing a public address with an internal one
    public async Task Connect_ToANameResolvingToTheHostsOwnAddress_Is403_AndNothingIsEverConnected(string name)
    {
        using var client = await _pair.ConnectToProxyAsync();
        var stream = client.GetStream();
        await stream.WriteAsync(E2EWait.Ascii($"CONNECT {name}:{_victim.Port} HTTP/1.1\r\nHost: {name}:{_victim.Port}\r\n\r\n"));

        var head = await Http.ReadHeadAsync(stream);

        // OPEN_FAIL(private_ip) -> 403 at the browser (docs/protocol.md section 6 rule 6 and the StatusForOpenFail table).
        Assert.Equal(403, head.Status);
        Assert.Equal(0, _victim.Accepted);

        // The host really did try (a stream was opened and failed on policy) and the domain was not recorded as if it had been visited.
        Assert.Equal(0, _pair.HostHandler.OpensOk);
        Assert.True(_pair.HostHandler.OpensFailed >= 1);
        Assert.DoesNotContain(name, _pair.Host.DomainsSeen);

        // And not one byte through the tunnel towards the destination.
        Assert.Equal(0, _pair.HostHandler.Counter.Up);
        Assert.Equal(0, _pair.HostHandler.Counter.Down);
    }

    [Fact]
    public async Task Connect_ToTheHostsOwnListener_StaysBlockedAcrossRepeatedAttempts()
    {
        // One attempt may fail for a transient reason; ten consecutive attempts prove the refusal is policy rather than chance.
        for (var i = 0; i < 10; i++)
        {
            using var client = await _pair.ConnectToProxyAsync();
            var stream = client.GetStream();
            await stream.WriteAsync(E2EWait.Ascii($"CONNECT self.test:{_victim.Port} HTTP/1.1\r\n\r\n"));
            Assert.Equal(403, (await Http.ReadHeadAsync(stream)).Status);
        }

        Assert.Equal(0, _victim.Accepted);
        Assert.Equal(0, _pair.HostHandler.OpensOk);
    }

    [Fact]
    public async Task Connect_ToAnIpLiteral_NeverEvenReachesTheHost()
    {
        // The address literal falls locally at the guest (ProxyRouter): no OPEN through the tunnel at all.
        var before = _pair.HostHandler.OpensFailed;
        foreach (var literal in new[] { "127.0.0.1", "[::1]", "10.0.0.1", "[::ffff:127.0.0.1]" })
        {
            using var client = await _pair.ConnectToProxyAsync();
            var stream = client.GetStream();
            await stream.WriteAsync(E2EWait.Ascii($"CONNECT {literal}:{_victim.Port} HTTP/1.1\r\n\r\n"));
            Assert.Equal(403, (await Http.ReadHeadAsync(stream)).Status);
        }

        Assert.Equal(before, _pair.HostHandler.OpensFailed);
        Assert.Equal(0, _victim.Accepted);
    }

    [Fact]
    public async Task Connect_ToTheGuestsOwnProxyPort_IsRejected()
    {
        // self.test resolves to 127.0.0.1 on the guest too: the proxy does not become a bridge to itself nor to the guest's listener.
        using var client = await _pair.ConnectToProxyAsync();
        var stream = client.GetStream();
        await stream.WriteAsync(E2EWait.Ascii($"CONNECT self.test:{_pair.ProxyPort} HTTP/1.1\r\n\r\n"));

        // The port is not within the entry's restriction (self.test:<victim>) => not through the tunnel => the direct path => blocked after resolution.
        Assert.Equal(403, (await Http.ReadHeadAsync(stream)).Status);
        Assert.Equal(0, _victim.Accepted);
    }

    [Fact]
    public async Task UnderTheProductionPolicy_EvenTheAllowlistedOriginIsRefused_BecauseItTooIsLoopbackHere()
    {
        // An explicit control: <c>site.test</c> is in the list and works in the rest of the E2E tests — because they exempt loopback.
        // Here the policy is the production one, so it too is refused. The point: the refusal above is caused by the address, not by the list nor by a broken connection.
        var serve = _pair.Origin.ServeOnceAsync("never served");
        using var client = await _pair.ConnectToProxyAsync();
        var stream = client.GetStream();
        await stream.WriteAsync(E2EWait.Ascii($"CONNECT site.test:{_pair.OriginPort} HTTP/1.1\r\n\r\n"));
        Assert.Equal(403, (await Http.ReadHeadAsync(stream)).Status);
        Assert.False(serve.IsCompleted);
    }

    [Fact]
    public async Task TheTunnelItselfIsStillAlive_AfterEveryRejection()
    {
        // A policy refusal does not kill the tunnel: both sessions stay Connected and the mux is open and accepts another stream.
        for (var i = 0; i < 3; i++)
        {
            using var client = await _pair.ConnectToProxyAsync();
            var stream = client.GetStream();
            await stream.WriteAsync(E2EWait.Ascii($"CONNECT self.test:{_victim.Port} HTTP/1.1\r\n\r\n"));
            Assert.Equal(403, (await Http.ReadHeadAsync(stream)).Status);
        }

        Assert.Equal(Josour.Core.Tunnel.TunnelState.Connected, _pair.Host.State);
        Assert.Equal(Josour.Core.Tunnel.TunnelState.Connected, _pair.Guest.State);
        Assert.False(_pair.Host.MuxClosed);
        Assert.False(_pair.Guest.MuxClosed);
        _ = Timeout;
    }
}
