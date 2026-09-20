using System.Net;
using System.Net.Sockets;

using Josour.Tunnel.Mux;

namespace Josour.Proxy.Tests;

/// <summary>
/// The direct path on the guest's side with the real address policy (without the loopback exemption the other tests use).
///
/// <para>
/// Why it matters: <see cref="ProxyRouter"/> refuses an address literal, but a name that <b>resolves</b> to an internal address used to pass
/// through to <c>Socket.ConnectAsync</c> before week four's hardening. The proxy listens on 127.0.0.1 and accepts from any local process
/// (the owner check only works when a browser is passed), so it made a bridge towards whatever this machine itself is listening on: the tunnel's listener during
/// the connect window, the relay's port, and any service on 127.0.0.1. Now it is refused after resolution and before any socket.
/// </para>
/// </summary>
public class ConnectProxyServerBlockedAddressTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    /// <summary>A listener standing in for what this machine itself is listening on (the tunnel's listener or a local relay port). It must never accept anything.</summary>
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

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("::1")]
    [InlineData("10.1.2.3")]
    [InlineData("192.168.7.7")]
    [InlineData("169.254.169.254")]
    [InlineData("::ffff:127.0.0.1")]
    [InlineData("64:ff9b::7f00:1")]
    [InlineData("2002:7f00:0001::")]
    [InlineData("::127.0.0.1")]
    public async Task Connect_NameResolvingToABlockedAddress_Is403_AfterResolution_WithNoConnection(string resolvesTo)
    {
        using var victim = new Victim();
        var resolver = new StubResolver().Map("evil.test", resolvesTo);
        var mux = new FakeMux();
        // The real address policy: no loopback exemption here.
        await using var proxy = Proxies.Start(mux, resolver, addressBlocker: Proxies.RealAddressPolicy);
        await using var client = await ProxyClient.ConnectAsync(proxy.Port);

        // evil.test is not in the list => the direct path, which is the path under test.
        await client.SendAsync($"CONNECT evil.test:{victim.Port} HTTP/1.1\r\nHost: evil.test:{victim.Port}\r\n\r\n");
        var head = await client.ReadHeadAsync();

        Assert.Equal(403, head.Status);
        Assert.Equal(1, resolver.Calls);                          // the refusal happened after resolution, not before
        Assert.Equal(0, victim.Accepted);                         // and no socket was opened towards the target
        Assert.Empty(mux.Opens);                                  // and the host was not asked to open it on our behalf
        Assert.Equal(1, proxy.Counters.Rejected);
        Assert.Equal(0, proxy.Counters.DirectConnects);
        Assert.True(await client.ClosedWithoutDataAsync());
    }

    [Fact]
    public async Task Connect_AnyBlockedAddressInTheAnswerRejectsAll_EvenWhenAPublicOneIsAlsoThere()
    {
        // DNS returns one public address and one internal: the contract's rule "any blocked address => refuse" (section 6 step 6)
        // stops Happy Eyeballs choosing the internal one after the check.
        using var victim = new Victim();
        var resolver = new StubResolver().Map("mixed.test", "93.184.216.34", "127.0.0.1");
        await using var proxy = Proxies.Start(new FakeMux(), resolver, addressBlocker: Proxies.RealAddressPolicy);
        await using var client = await ProxyClient.ConnectAsync(proxy.Port);

        await client.SendAsync($"CONNECT mixed.test:{victim.Port} HTTP/1.1\r\n\r\n");
        Assert.Equal(403, (await client.ReadHeadAsync()).Status);
        Assert.Equal(0, victim.Accepted);
    }

    [Fact]
    public async Task Http_PlainRequestToABlockedName_Is403_WithNoConnection()
    {
        // The same rule on the direct http:// path, not on CONNECT alone.
        using var victim = new Victim();
        var resolver = new StubResolver().Map("evil.test", "127.0.0.1");
        await using var proxy = Proxies.Start(new FakeMux(), resolver, addressBlocker: Proxies.RealAddressPolicy);
        await using var client = await ProxyClient.ConnectAsync(proxy.Port);

        await client.SendAsync($"GET http://evil.test:{victim.Port}/x HTTP/1.1\r\nHost: evil.test:{victim.Port}\r\n\r\n");
        Assert.Equal(403, (await client.ReadHeadAsync()).Status);
        Assert.Equal(1, resolver.Calls);
        Assert.Equal(0, victim.Accepted);
        Assert.Equal(0, proxy.Counters.DirectHttpRequests);
    }

    [Fact]
    public async Task Connect_ToTheProxysOwnPort_IsRejected_SoItCannotLoopBackIntoItself()
    {
        var resolver = new StubResolver().Map("loop.test", "127.0.0.1");
        await using var proxy = Proxies.Start(new FakeMux(), resolver, addressBlocker: Proxies.RealAddressPolicy);
        await using var client = await ProxyClient.ConnectAsync(proxy.Port);

        await client.SendAsync($"CONNECT loop.test:{proxy.Port} HTTP/1.1\r\n\r\n");
        Assert.Equal(403, (await client.ReadHeadAsync()).Status);
        Assert.Equal(1, proxy.Counters.Accepted); // one connection only: the proxy did not connect to itself
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("[::1]")]
    [InlineData("10.0.0.1")]
    [InlineData("[::ffff:169.254.169.254]")]
    [InlineData("[64:ff9b::a00:1]")]
    public async Task Connect_ToAnIpLiteral_IsRejectedBeforeAnyResolution(string literal)
    {
        var resolver = new StubResolver();
        await using var proxy = Proxies.Start(new FakeMux(), resolver, addressBlocker: Proxies.RealAddressPolicy);
        await using var client = await ProxyClient.ConnectAsync(proxy.Port);

        await client.SendAsync($"CONNECT {literal}:443 HTTP/1.1\r\n\r\n");
        Assert.Equal(403, (await client.ReadHeadAsync()).Status);
        Assert.Equal(0, resolver.Calls); // an address literal falls at the first step: no DNS and no socket
    }

    [Fact]
    public async Task Connect_ToAPublicAddress_StillWorks_UnderTheRealPolicy()
    {
        // Proving the hardening did not close the direct path entirely: a public address is reached as usual.
        using var origin = new LocalOrigin();
        var accept = origin.AcceptAsync();
        var resolver = new StubResolver().Map("public.test", "127.0.0.1");
        // We keep the real policy except for loopback, because there is no way to have a genuinely public origin inside the test.
        await using var proxy = Proxies.Start(new FakeMux(), resolver, addressBlocker: Proxies.BlockedExceptLoopback);
        await using var client = await ProxyClient.ConnectAsync(proxy.Port);

        await client.SendAsync($"CONNECT public.test:{origin.Port} HTTP/1.1\r\n\r\n");
        Assert.Equal(200, (await client.ReadHeadAsync()).Status);
        using var accepted = await accept.WaitAsync(Timeout);
        Assert.Equal(1, proxy.Counters.DirectConnects);
    }
}
