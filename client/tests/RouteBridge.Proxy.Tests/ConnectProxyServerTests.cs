using System.Net;
using System.Net.Sockets;
using System.Text;
using RouteBridge.Tunnel.Mux;

namespace RouteBridge.Proxy.Tests;

public class ConnectProxyServerTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task Connect_Allowed_OpensViaMux_And200OnlyAfterOpenOk()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var (near, far) = await TcpPair.CreateAsync();
        using var _far = far;
        var mux = new FakeMux
        {
            OnOpen = async (_, _, ct) => { await gate.Task.WaitAsync(ct); return MuxOpenResult.Ok(near); },
        };
        await using var proxy = Proxies.Start(mux);
        await using var client = await ProxyClient.ConnectAsync(proxy.Port);

        await client.SendAsync("CONNECT Allowed.Example:443 HTTP/1.1\r\nHost: allowed.example:443\r\nProxy-Connection: keep-alive\r\n\r\n");

        // لا 200 قبل OPEN_OK
        using (var cts = new CancellationTokenSource(400))
        {
            var one = new byte[1];
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.Stream.ReadAsync(one, cts.Token).AsTask());
        }
        Assert.Equal(new[] { ("allowed.example", 443) }, mux.Opens);

        gate.SetResult();
        var head = await client.ReadHeadAsync();
        Assert.Equal(200, head.Status);
        Assert.Empty(head.Remainder);

        await client.SendAsync("tls-bytes");
        var buffer = new byte[9];
        await far.ReceiveAsync(buffer, SocketFlags.None).WaitAsync(Timeout);
        Assert.Equal("tls-bytes", Encoding.ASCII.GetString(buffer));
        await far.SendAsync(Encoding.ASCII.GetBytes("reply"), SocketFlags.None);
        var reply = new byte[5];
        await client.Stream.ReadExactlyAsync(reply).AsTask().WaitAsync(Timeout);
        Assert.Equal("reply", Encoding.ASCII.GetString(reply));
        Assert.Equal(1, proxy.Counters.Tunneled);
    }

    [Theory]
    [InlineData(OpenFailReason.PrivateIp, 403)]
    [InlineData(OpenFailReason.PortNotAllowed, 403)]
    [InlineData(OpenFailReason.IpLiteral, 403)]
    [InlineData(OpenFailReason.DnsFailed, 502)]
    [InlineData(OpenFailReason.ConnectFailed, 502)]
    [InlineData(OpenFailReason.Limit, 503)]
    public async Task Connect_OpenFail_MapsToHonestStatus(OpenFailReason reason, int expected)
    {
        var mux = new FakeMux { OnOpen = (_, _, _) => Task.FromResult(MuxOpenResult.Fail(reason)) };
        await using var proxy = Proxies.Start(mux);
        await using var client = await ProxyClient.ConnectAsync(proxy.Port);
        await client.SendAsync("CONNECT allowed.example:443 HTTP/1.1\r\n\r\n");
        var head = await client.ReadHeadAsync();
        Assert.Equal(expected, head.Status);
        Assert.Equal("close", head.Headers["Connection"]);
        Assert.True(await client.ClosedWithoutDataAsync());
    }

    [Fact]
    public async Task Connect_OpenFailNotAllowed_FallsBackToDirect()
    {
        using var origin = new LocalOrigin();
        var accept = origin.AcceptAsync();
        var mux = new FakeMux { OnOpen = (_, _, _) => Task.FromResult(MuxOpenResult.Fail(OpenFailReason.NotAllowed)) };
        var resolver = new StubResolver().Map("allowed.example", "127.0.0.1");
        await using var proxy = Proxies.Start(mux, resolver, allowedPorts: new[] { origin.Port });
        await using var client = await ProxyClient.ConnectAsync(proxy.Port);

        await client.SendAsync($"CONNECT allowed.example:{origin.Port} HTTP/1.1\r\n\r\n");
        var head = await client.ReadHeadAsync();
        Assert.Equal(200, head.Status);
        Assert.Single(mux.Opens);
        using var far = await accept.WaitAsync(Timeout);
        await client.SendAsync("x");
        var b = new byte[1];
        await far.ReceiveAsync(b, SocketFlags.None).WaitAsync(Timeout);
        Assert.Equal((byte)'x', b[0]);
        Assert.Equal(1, proxy.Counters.DirectConnects);
    }

    [Fact]
    public async Task Connect_NotAllowlisted_GoesDirect_WithoutTouchingMux()
    {
        using var origin = new LocalOrigin();
        var accept = origin.AcceptAsync();
        var mux = new FakeMux();
        var resolver = new StubResolver().Map("direct.test", "127.0.0.1");
        await using var proxy = Proxies.Start(mux, resolver);
        await using var client = await ProxyClient.ConnectAsync(proxy.Port);

        await client.SendAsync($"CONNECT direct.test:{origin.Port} HTTP/1.1\r\nHost: direct.test:{origin.Port}\r\n\r\nearly");
        var head = await client.ReadHeadAsync();
        Assert.Equal(200, head.Status);
        Assert.Empty(mux.Opens);
        using var far = await accept.WaitAsync(Timeout);
        var buffer = new byte[5];
        await far.ReceiveAsync(buffer, SocketFlags.None).WaitAsync(Timeout);
        Assert.Equal("early", Encoding.ASCII.GetString(buffer)); // البايتات التي تلت الرأس تُمرر

        far.Shutdown(SocketShutdown.Send);
        Assert.Empty(await client.ReadToEndAsync()); // إغلاق الأصل ينتقل إلى المتصفح
    }

    [Theory]
    [InlineData("10.0.0.1:443")]
    [InlineData("[::1]:443")]
    [InlineData("127.0.0.1:80")]
    [InlineData("192.168.1.1:8443")]
    [InlineData("8.8.8.8:443")]
    [InlineData("[2001:db8::1]:443")]
    [InlineData("localhost:443")]
    [InlineData("foo.localhost:443")]
    public async Task Connect_IpLiteralOrLocal_403_WithoutAnyConnect(string authority)
    {
        var mux = new FakeMux();
        var resolver = new StubResolver();
        await using var proxy = Proxies.Start(mux, resolver);
        await using var client = await ProxyClient.ConnectAsync(proxy.Port);
        await client.SendAsync($"CONNECT {authority} HTTP/1.1\r\n\r\n");
        var head = await client.ReadHeadAsync();
        Assert.Equal(403, head.Status);
        Assert.Empty(mux.Opens);
        Assert.Equal(0, resolver.Calls);
        Assert.Equal(1, proxy.Counters.Rejected);
    }

    [Fact]
    public async Task Connect_MuxClosed_502()
    {
        var mux = new FakeMux { Closed = true };
        await using var proxy = Proxies.Start(mux);
        await using var client = await ProxyClient.ConnectAsync(proxy.Port);
        await client.SendAsync("CONNECT allowed.example:443 HTTP/1.1\r\n\r\n");
        Assert.Equal(502, (await client.ReadHeadAsync()).Status);
    }

    [Fact]
    public async Task Connect_DirectRefused_502()
    {
        var port = LocalOrigin.ClosedPort();
        var resolver = new StubResolver().Map("direct.test", "127.0.0.1");
        await using var proxy = Proxies.Start(new FakeMux(), resolver);
        await using var client = await ProxyClient.ConnectAsync(proxy.Port);
        await client.SendAsync($"CONNECT direct.test:{port} HTTP/1.1\r\n\r\n");
        Assert.Equal(502, (await client.ReadHeadAsync()).Status);
    }

    [Fact]
    public async Task Connect_DirectDnsFailure_502()
    {
        await using var proxy = Proxies.Start(new FakeMux(), new StubResolver());
        await using var client = await ProxyClient.ConnectAsync(proxy.Port);
        await client.SendAsync("CONNECT nowhere.test:443 HTTP/1.1\r\n\r\n");
        Assert.Equal(502, (await client.ReadHeadAsync()).Status);
    }

    [Theory]
    [InlineData("GET http://check.routebridge/ HTTP/1.1\r\nHost: check.routebridge\r\n\r\n")]
    [InlineData("GET http://check.routebridge/any/path?x=1 HTTP/1.1\r\nHost: check.routebridge\r\nProxy-Connection: keep-alive\r\n\r\n")]
    [InlineData("GET / HTTP/1.1\r\nHost: check.routebridge\r\n\r\n")]
    public async Task Probe_ServedWithPeerIp_AndProbeHitRaised(string request)
    {
        var mux = new FakeMux();
        await using var proxy = Proxies.Start(mux);
        DateTimeOffset? hit = null;
        proxy.ProbeHit += t => hit = t;
        Assert.Null(proxy.FirstProbeHitAt);

        await using var client = await ProxyClient.ConnectAsync(proxy.Port);
        await client.SendAsync(request);
        var head = await client.ReadHeadAsync();
        Assert.Equal(200, head.Status);
        Assert.StartsWith("text/html", head.Headers["Content-Type"]);
        var body = Encoding.UTF8.GetString(await client.ReadBodyAsync(head));
        Assert.Contains("Tunnel active. Sites will see: 203.0.113.9", body);
        Assert.Contains("النفق نشط", body);
        Assert.NotNull(hit);
        Assert.NotNull(proxy.FirstProbeHitAt);
        Assert.Equal(1, proxy.Counters.ProbeHits);
        Assert.Empty(mux.Opens);
        Assert.True(await client.ClosedWithoutDataAsync());
    }

    [Theory]
    [InlineData("http://allowed.example/path?q=1", "https://allowed.example/path?q=1")]
    [InlineData("http://sub.allowed.example/", "https://sub.allowed.example/")]
    [InlineData("http://ALLOWED.example", "https://allowed.example/")]
    [InlineData("http://allowed.example:8080/x", "https://allowed.example:8080/x")]
    [InlineData("http://portal.example/", "https://portal.example/")]
    public async Task Http_Allowlisted_307ToHttps_NoBytesViaTunnel(string url, string location)
    {
        var mux = new FakeMux();
        var resolver = new StubResolver();
        await using var proxy = Proxies.Start(mux, resolver);
        await using var client = await ProxyClient.ConnectAsync(proxy.Port);
        await client.SendAsync($"GET {url} HTTP/1.1\r\nHost: allowed.example\r\n\r\n");
        var head = await client.ReadHeadAsync();
        Assert.Equal(307, head.Status);
        Assert.Equal(location, head.Headers["Location"]);
        Assert.Empty(mux.Opens);
        Assert.Equal(0, resolver.Calls);
    }

    [Fact]
    public async Task Http_Direct_OneRequestPerConnection_OriginForm_ConnectionClose()
    {
        using var origin = new HttpOrigin();
        var serve = origin.ServeOnceAsync("hello from origin");
        var resolver = new StubResolver().Map("direct.test", "127.0.0.1");
        await using var proxy = Proxies.Start(new FakeMux(), resolver);
        await using var client = await ProxyClient.ConnectAsync(proxy.Port);

        await client.SendAsync($"GET http://direct.test:{origin.Port}/hello?x=1 HTTP/1.1\r\nHost: direct.test:{origin.Port}\r\nProxy-Connection: keep-alive\r\nUser-Agent: t\r\nAccept: */*\r\n\r\n");
        var head = await client.ReadHeadAsync();
        Assert.Equal(200, head.Status);
        Assert.Equal("hello from origin", Encoding.UTF8.GetString(await client.ReadBodyAsync(head)));
        Assert.True(await client.ClosedWithoutDataAsync()); // اتصال واحد = طلب واحد
        await serve.WaitAsync(Timeout);

        Assert.NotNull(origin.ReceivedHead);
        Assert.StartsWith("GET /hello?x=1 HTTP/1.1\r\n", origin.ReceivedHead);
        Assert.DoesNotContain("Proxy-Connection", origin.ReceivedHead, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Connection: close", origin.ReceivedHead);
        Assert.Contains("User-Agent: t", origin.ReceivedHead);
        Assert.Contains($"Host: direct.test:{origin.Port}", origin.ReceivedHead);
        Assert.Equal(1, proxy.Counters.DirectHttpRequests);
    }

    [Theory]
    [InlineData("http://10.0.0.1/")]
    [InlineData("http://[::1]/")]
    [InlineData("http://localhost/")]
    public async Task Http_IpLiteralOrLocal_403(string url)
    {
        await using var proxy = Proxies.Start(new FakeMux());
        await using var client = await ProxyClient.ConnectAsync(proxy.Port);
        await client.SendAsync($"GET {url} HTTP/1.1\r\n\r\n");
        Assert.Equal(403, (await client.ReadHeadAsync()).Status);
    }

    [Theory]
    [InlineData("GET /nohost HTTP/1.1\r\n\r\n")]
    [InlineData("GET https://allowed.example/ HTTP/1.1\r\n\r\n")]
    [InlineData("BROKEN\r\n\r\n")]
    [InlineData("CONNECT allowed.example:443:1 HTTP/1.1\r\n\r\n")]
    [InlineData("CONNECT allowed.example:99999 HTTP/1.1\r\n\r\n")]
    public async Task Malformed_400(string request)
    {
        await using var proxy = Proxies.Start(new FakeMux());
        await using var client = await ProxyClient.ConnectAsync(proxy.Port);
        await client.SendAsync(request);
        Assert.Equal(400, (await client.ReadHeadAsync()).Status);
    }

    [Fact]
    public async Task OwnerPid_NotOwnedByBrowser_ClosedWithoutResponse()
    {
        var browser = new FakeBrowser();
        browser.Owned.Add(1);
        var checker = new FakePidChecker { Pid = 4242 };
        await using var proxy = Proxies.Start(new FakeMux(), browser: browser, checker: checker);
        // الرفض يغلق المقبس فورًا؛ قد يصل FIN أو RST، وقد يضرب RST عند الاتصال نفسه تحت الحمل.
        // العدّادات هي التأكيد الحقيقي: طُلب الفحص مرة، ورُفض الاتصال، ولم تُخدَم صفحة الفحص.
        try
        {
            await using var client = await ProxyClient.ConnectAsync(proxy.Port);
            try { await client.SendAsync("GET http://check.routebridge/ HTTP/1.1\r\n\r\n"); }
            catch (IOException) { }
            catch (SocketException) { }
            Assert.True(await client.ClosedWithoutDataAsync());
        }
        catch (SocketException) { /* أُعيد ضبط الاتصال فورًا: النتيجة نفسها */ }

        await Proxies.WaitForAsync(() => proxy.Counters.RejectedByOwner == 1);
        Assert.Equal(1, checker.Calls);
        Assert.Equal(1, proxy.Counters.RejectedByOwner);
        Assert.Equal(0, proxy.Counters.ProbeHits);
    }

    [Fact]
    public async Task OwnerPid_OwnedByBrowser_Served()
    {
        var browser = new FakeBrowser();
        browser.Owned.Add(4242);
        var checker = new FakePidChecker { Pid = 4242 };
        await using var proxy = Proxies.Start(new FakeMux(), browser: browser, checker: checker);
        await using var client = await ProxyClient.ConnectAsync(proxy.Port);
        await client.SendAsync("GET http://check.routebridge/ HTTP/1.1\r\n\r\n");
        Assert.Equal(200, (await client.ReadHeadAsync()).Status);
        Assert.Equal(0, proxy.Counters.RejectedByOwner);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task OwnerPid_Unknown_FollowsRejectUnknownOwner(bool rejectUnknown, bool served)
    {
        var browser = new FakeBrowser();
        await using var proxy = Proxies.Start(new FakeMux(), browser: browser, checker: new FakePidChecker { Pid = null }, rejectUnknown: rejectUnknown);
        if (served)
        {
            await using var client = await ProxyClient.ConnectAsync(proxy.Port);
            await client.SendAsync("GET http://check.routebridge/ HTTP/1.1\r\n\r\n");
            Assert.Equal(200, (await client.ReadHeadAsync()).Status);
            return;
        }

        // الرفض يغلق المقبس فورًا: قد يصل FIN أو RST، وقد يضرب RST عند الاتصال نفسه تحت الحمل.
        try
        {
            await using var client = await ProxyClient.ConnectAsync(proxy.Port);
            try { await client.SendAsync("GET http://check.routebridge/ HTTP/1.1\r\n\r\n"); }
            catch (IOException) { }
            catch (SocketException) { }
            Assert.True(await client.ClosedWithoutDataAsync());
        }
        catch (SocketException) { /* أُعيد ضبط الاتصال فورًا: النتيجة نفسها */ }
    }

    [Fact]
    public async Task NoBrowserSession_AcceptsEverything_WithoutCheckingPid()
    {
        var checker = new FakePidChecker { Pid = 1 };
        await using var proxy = Proxies.Start(new FakeMux(), checker: checker);
        await using var client = await ProxyClient.ConnectAsync(proxy.Port);
        await client.SendAsync("GET http://check.routebridge/ HTTP/1.1\r\n\r\n");
        Assert.Equal(200, (await client.ReadHeadAsync()).Status);
        Assert.Equal(0, checker.Calls);
    }

    [Fact]
    public async Task StopAccepting_RefusesNewConnections_KeepsPort()
    {
        await using var proxy = Proxies.Start(new FakeMux());
        Assert.True(proxy.IsAccepting);
        proxy.StopAccepting();
        Assert.False(proxy.IsAccepting);
        using var probe = new TcpClient();
        await Assert.ThrowsAnyAsync<SocketException>(() => probe.ConnectAsync(IPAddress.Loopback, proxy.Port).WaitAsync(Timeout));
    }

    [Fact]
    public async Task ListensOnLoopbackEphemeralPort()
    {
        await using var proxy = new ConnectProxyServer(new ConnectProxyOptions { Allowlist = Core.Allowlist.AllowlistMatcher.Parse(1, Array.Empty<string>()) });
        Assert.Equal(IPAddress.Loopback, proxy.LocalEndPoint.Address);
        Assert.InRange(proxy.Port, 1024, 65535);
    }
}
