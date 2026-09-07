using System.Net;
using System.Net.Sockets;
using Josour.Core.Allowlist;
using Josour.Core.Net;
using Josour.Tunnel;
using Josour.Tunnel.Mux;

namespace Josour.Proxy.Tests;

/// <summary>الجسر بين <see cref="TunnelSession"/> والـ Proxy المحلي: المنفذ، رابط الفحص، إشارة الوصول، وخطوات الإيقاف.</summary>
public class ProxyTunnelAdapterTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private static TunnelProxyContext Context(IMuxConnection mux, IHostResolver? resolver = null, string[]? entries = null)
        => new(AllowlistMatcher.Parse(1, entries ?? Proxies.DefaultEntries), new[] { 80, 443 }, resolver ?? new StubResolver(), "203.0.113.9", mux);

    [Fact]
    public async Task Create_ExposesPortAndProbeUrl_AndListensAfterStart()
    {
        await using var adapter = ProxyTunnelAdapter.Create(Context(new FakeMux()));
        Assert.InRange(adapter.Port, 1, 65535);
        Assert.Equal("http://check.josour/", adapter.ProbeUrl);
        Assert.Equal(ProbePage.Url, adapter.ProbeUrl);

        adapter.Start();
        await using var client = await ProxyClient.ConnectAsync(adapter.Port);
        // IP حرفي محلي يُرفض قبل النفق (دفاع في العمق على جانب المستخدم)
        await client.SendAsync("CONNECT 192.168.0.5:443 HTTP/1.1\r\n\r\n");
        Assert.Equal(403, (await client.ReadHeadAsync()).Status);
    }

    [Fact]
    public async Task Create_PassesTheMuxAndAllowlist_FromTheContext()
    {
        var mux = new FakeMux();
        var (near, far) = await TcpPair.CreateAsync();
        using var _far = far;
        mux.OnOpen = (_, _, _) => Task.FromResult(MuxOpenResult.Ok(near));

        await using var adapter = ProxyTunnelAdapter.Create(Context(mux));
        adapter.Start();
        await using var client = await ProxyClient.ConnectAsync(adapter.Port);
        await client.SendAsync("CONNECT allowed.example:443 HTTP/1.1\r\n\r\n");

        Assert.Equal(200, (await client.ReadHeadAsync()).Status);
        Assert.Equal(new[] { ("allowed.example", 443) }, mux.Opens);
    }

    [Fact]
    public async Task ProbeSeen_IsRaised_WhenTheProbePageIsFetched()
    {
        var hits = 0;
        await using var adapter = ProxyTunnelAdapter.Create(Context(new FakeMux()));
        adapter.ProbeSeen += () => Interlocked.Increment(ref hits);
        adapter.Start();

        await using var client = await ProxyClient.ConnectAsync(adapter.Port);
        await client.SendAsync($"GET {adapter.ProbeUrl} HTTP/1.1\r\nHost: {ProbePage.Host}\r\n\r\n");
        var head = await client.ReadHeadAsync();

        Assert.Equal(200, head.Status);
        Assert.Equal(1, hits);
        Assert.NotNull(((ProxyTunnelAdapter)adapter).Server.FirstProbeHitAt);
    }

    [Fact]
    public async Task StopAccepting_ClosesThePort_ThenStopAndDisposeAreIdempotent()
    {
        var adapter = ProxyTunnelAdapter.Create(Context(new FakeMux()));
        adapter.Start();
        var port = adapter.Port;
        await using (var open = await ProxyClient.ConnectAsync(port)) { }

        adapter.StopAccepting();
        Assert.True(await PortRefusedAsync(port));

        await adapter.StopAsync();
        await adapter.StopAsync();
        await adapter.DisposeAsync();
        await adapter.DisposeAsync();
    }

    [Fact]
    public async Task Create_WithBrowser_AppliesOwnerPidCheck()
    {
        var browser = new FakeBrowser(); // لا يملك أي PID
        var checker = new FakePidChecker { Pid = 4242 };
        await using var adapter = ProxyTunnelAdapter.Create(Context(new FakeMux()), browser, checker);
        adapter.Start();

        // الرفض يغلق المقبس فورًا؛ قد تفشل الكتابة أو الاتصال نفسه بإعادة ضبط، والعدّادات هي التأكيد.
        try
        {
            await using var client = await ProxyClient.ConnectAsync(adapter.Port);
            try { await client.SendAsync("CONNECT allowed.example:443 HTTP/1.1\r\n\r\n"); }
            catch (IOException) { }
            catch (SocketException) { }
        }
        catch (SocketException) { }

        var server = ((ProxyTunnelAdapter)adapter).Server;
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (server.Counters.RejectedByOwner == 0 && DateTime.UtcNow < deadline) await Task.Delay(20);
        Assert.Equal(1, server.Counters.RejectedByOwner);
        Assert.Equal(1, checker.Calls);
        Assert.Equal(0, server.Counters.Tunneled);
    }

    private static async Task<bool> PortRefusedAsync(int port)
    {
        using var client = new TcpClient();
        try
        {
            await client.ConnectAsync(IPAddress.Loopback, port).WaitAsync(Timeout);
            return false;
        }
        catch (SocketException) { return true; }
    }
}
