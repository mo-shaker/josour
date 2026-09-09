using System.Net.Sockets;
using System.Text;
using Josour.Core.Net;
using Josour.Tunnel.Mux;

namespace Josour.Proxy.Tests;

/// <summary>
/// المسار المباشر خلف Proxy إجباري (الخطة 8.5): CONNECT إلى Proxy النظام لطلبات النفق، وabsolute-URI
/// لطلبات http العادية، مع قائمة التجاوز. المسار المسموح به لا يمر من هنا إطلاقًا.
/// </summary>
public class SystemProxyPathTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    // ---------- CONNECT ----------

    [Fact]
    public async Task Connect_NotAllowlisted_GoesThroughTheSystemProxy()
    {
        using var origin = new LocalOrigin();
        var accept = origin.AcceptAsync();
        await using var upstream = new FakeUpstreamProxy();
        var mux = new FakeMux();
        var resolver = new StubResolver().Map("direct.test", "127.0.0.1");
        await using var proxy = Proxies.Start(mux, resolver, systemProxy: upstream.AsResolver());
        await using var client = await ProxyClient.ConnectAsync(proxy.Port);

        await client.SendAsync($"CONNECT direct.test:{origin.Port} HTTP/1.1\r\nHost: direct.test:{origin.Port}\r\n\r\nearly");
        var head = await client.ReadHeadAsync();
        Assert.Equal(200, head.Status);
        Assert.Empty(mux.Opens);

        using var far = await accept.WaitAsync(Timeout);
        var buffer = new byte[5];
        await far.ReceiveAsync(buffer, SocketFlags.None).WaitAsync(Timeout);
        Assert.Equal("early", Encoding.ASCII.GetString(buffer));

        Assert.Equal($"CONNECT direct.test:{origin.Port} HTTP/1.1", Assert.Single(upstream.RequestLines));
        Assert.Equal(1, proxy.Counters.ViaSystemProxy);
        Assert.Equal(1, proxy.Counters.DirectConnects);
    }

    [Fact]
    public async Task Connect_WithoutASystemProxy_OpensARawSocket()
    {
        using var origin = new LocalOrigin();
        var accept = origin.AcceptAsync();
        await using var upstream = new FakeUpstreamProxy();
        var resolver = new StubResolver().Map("direct.test", "127.0.0.1");
        // لا Proxy مضبوط: السلوك كما كان قبل هذا الأسبوع.
        await using var proxy = Proxies.Start(new FakeMux(), resolver, systemProxy: NoSystemProxy.Instance);
        await using var client = await ProxyClient.ConnectAsync(proxy.Port);

        await client.SendAsync($"CONNECT direct.test:{origin.Port} HTTP/1.1\r\n\r\n");
        Assert.Equal(200, (await client.ReadHeadAsync()).Status);
        using var far = await accept.WaitAsync(Timeout);
        Assert.Equal(0, upstream.Connections);
        Assert.Equal(0, proxy.Counters.ViaSystemProxy);
        Assert.Equal(1, proxy.Counters.DirectConnects);
    }

    [Fact]
    public async Task Connect_BypassedDestination_SkipsTheSystemProxy()
    {
        using var origin = new LocalOrigin();
        var accept = origin.AcceptAsync();
        await using var upstream = new FakeUpstreamProxy();
        var resolver = new StubResolver().Map("direct.test", "127.0.0.1");
        var bypassing = upstream.AsResolver(uri => uri.Host == "direct.test");
        await using var proxy = Proxies.Start(new FakeMux(), resolver, systemProxy: bypassing);
        await using var client = await ProxyClient.ConnectAsync(proxy.Port);

        await client.SendAsync($"CONNECT direct.test:{origin.Port} HTTP/1.1\r\n\r\n");
        Assert.Equal(200, (await client.ReadHeadAsync()).Status);
        using var far = await accept.WaitAsync(Timeout);
        Assert.Equal(0, upstream.Connections);
        Assert.Equal(0, proxy.Counters.ViaSystemProxy);
    }

    [Fact]
    public async Task Connect_AllowlistedHost_NeverTouchesTheSystemProxy()
    {
        // العقد: ما يمر عبر النفق يخرج من عند المضيف، ولا شأن لـ Proxy جهاز المستخدم به.
        await using var upstream = new FakeUpstreamProxy();
        var (near, far) = await TcpPair.CreateAsync();
        using (far)
        {
            var mux = new FakeMux { OnOpen = (_, _, _) => Task.FromResult(MuxOpenResult.Ok(near)) };
            await using var proxy = Proxies.Start(mux, systemProxy: upstream.AsResolver());
            await using var client = await ProxyClient.ConnectAsync(proxy.Port);

            await client.SendAsync("CONNECT allowed.example:443 HTTP/1.1\r\n\r\n");
            Assert.Equal(200, (await client.ReadHeadAsync()).Status);
            Assert.Single(mux.Opens);
            Assert.Equal(0, upstream.Connections);
            Assert.Equal(0, proxy.Counters.ViaSystemProxy);
            Assert.Equal(1, proxy.Counters.Tunneled);
        }
    }

    [Fact]
    public async Task Connect_UpstreamRefuses_502()
    {
        using var origin = new LocalOrigin();
        await using var upstream = new FakeUpstreamProxy { RefuseConnect = true };
        var resolver = new StubResolver().Map("direct.test", "127.0.0.1");
        await using var proxy = Proxies.Start(new FakeMux(), resolver, systemProxy: upstream.AsResolver());
        await using var client = await ProxyClient.ConnectAsync(proxy.Port);

        await client.SendAsync($"CONNECT direct.test:{origin.Port} HTTP/1.1\r\n\r\n");
        Assert.Equal(502, (await client.ReadHeadAsync()).Status);
    }

    [Fact]
    public async Task Connect_UpstreamUnreachable_502()
    {
        var dead = new StaticSystemProxy(new SystemProxyEndpoint("127.0.0.1", LocalOrigin.ClosedPort()));
        var resolver = new StubResolver().Map("direct.test", "127.0.0.1");
        await using var proxy = Proxies.Start(new FakeMux(), resolver, systemProxy: dead);
        await using var client = await ProxyClient.ConnectAsync(proxy.Port);

        await client.SendAsync("CONNECT direct.test:443 HTTP/1.1\r\n\r\n");
        Assert.Equal(502, (await client.ReadHeadAsync()).Status);
    }

    [Fact]
    public async Task Connect_UpstreamDemandsAuth_407IsRelayedThenCredentialsAreForwarded()
    {
        using var origin = new LocalOrigin();
        var accept = origin.AcceptAsync();
        await using var upstream = new FakeUpstreamProxy { RequireAuthentication = true };
        var resolver = new StubResolver().Map("direct.test", "127.0.0.1");
        await using var proxy = Proxies.Start(new FakeMux(), resolver, systemProxy: upstream.AsResolver());

        // 1) بلا بيانات اعتماد: يصل 407 ومعه Proxy-Authenticate كي يعرف المتصفح كيف يصادق.
        await using (var first = await ProxyClient.ConnectAsync(proxy.Port))
        {
            await first.SendAsync($"CONNECT direct.test:{origin.Port} HTTP/1.1\r\n\r\n");
            var head = await first.ReadHeadAsync();
            Assert.Equal(407, head.Status);
            Assert.Equal("Basic realm=\"corp\"", head.Headers["Proxy-Authenticate"]);
        }

        // 2) المتصفح يعيد المحاولة بـ Proxy-Authorization؛ نمررها إلى الـ Proxy الأعلى فينجح.
        await using var second = await ProxyClient.ConnectAsync(proxy.Port);
        await second.SendAsync($"CONNECT direct.test:{origin.Port} HTTP/1.1\r\nProxy-Authorization: Basic dTpw\r\n\r\n");
        Assert.Equal(200, (await second.ReadHeadAsync()).Status);
        using var far = await accept.WaitAsync(Timeout);
        Assert.Equal(new string?[] { null, "Basic dTpw" }, upstream.Authorizations);
    }

    [Fact]
    public async Task Connect_BytesArrivingWithTheUpstreamHead_ReachTheBrowser()
    {
        using var origin = new LocalOrigin();
        var accept = origin.AcceptAsync();
        await using var upstream = new FakeUpstreamProxy { ConnectRemainder = Encoding.ASCII.GetBytes("SRV") };
        var resolver = new StubResolver().Map("direct.test", "127.0.0.1");
        await using var proxy = Proxies.Start(new FakeMux(), resolver, systemProxy: upstream.AsResolver());
        await using var client = await ProxyClient.ConnectAsync(proxy.Port);

        await client.SendAsync($"CONNECT direct.test:{origin.Port} HTTP/1.1\r\n\r\n");
        var head = await client.ReadHeadAsync();
        Assert.Equal(200, head.Status);
        using var far = await accept.WaitAsync(Timeout);

        var seen = head.Remainder;
        while (seen.Length < 3)
        {
            var buffer = new byte[3 - seen.Length];
            var n = await client.Stream.ReadAsync(buffer).AsTask().WaitAsync(Timeout);
            Assert.True(n > 0, "the bytes that came with the upstream head were lost");
            seen = seen.Concat(buffer.Take(n)).ToArray();
        }
        Assert.Equal("SRV", Encoding.ASCII.GetString(seen));
    }

    // ---------- http:// عادي ----------

    [Fact]
    public async Task Http_NotAllowlisted_GoesThroughTheSystemProxyInAbsoluteUriForm()
    {
        await using var upstream = new FakeUpstreamProxy { HttpBody = "served by the corporate proxy" };
        var resolver = new StubResolver().Map("direct.test", "127.0.0.1");
        await using var proxy = Proxies.Start(new FakeMux(), resolver, systemProxy: upstream.AsResolver());
        await using var client = await ProxyClient.ConnectAsync(proxy.Port);

        await client.SendAsync("GET http://direct.test/hello?x=1 HTTP/1.1\r\nHost: direct.test\r\nProxy-Connection: keep-alive\r\nUser-Agent: t\r\n\r\n");
        var head = await client.ReadHeadAsync();
        Assert.Equal(200, head.Status);
        Assert.Equal("served by the corporate proxy", Encoding.UTF8.GetString(await client.ReadBodyAsync(head)));

        // الهدف يبقى absolute-URI (الـ Proxy الأعلى يحتاجه)، وProxy-Connection يُحذف.
        Assert.Equal("GET http://direct.test/hello?x=1 HTTP/1.1", Assert.Single(upstream.RequestLines));
        Assert.Equal(0, resolver.Calls); // لا حل اسم للوجهة: الـ Proxy هو من يحلها
        Assert.Equal(1, proxy.Counters.ViaSystemProxy);
        Assert.Equal(1, proxy.Counters.DirectHttpRequests);
    }

    [Fact]
    public async Task Http_NonDefaultPort_KeepsThePortInTheAbsoluteUri()
    {
        await using var upstream = new FakeUpstreamProxy();
        var resolver = new StubResolver();
        await using var proxy = Proxies.Start(new FakeMux(), resolver, systemProxy: upstream.AsResolver(), allowedPorts: new[] { 80, 443 });
        await using var client = await ProxyClient.ConnectAsync(proxy.Port);

        await client.SendAsync("GET http://direct.test:8080/p HTTP/1.1\r\nHost: direct.test:8080\r\n\r\n");
        Assert.Equal(200, (await client.ReadHeadAsync()).Status);
        Assert.Equal("GET http://direct.test:8080/p HTTP/1.1", Assert.Single(upstream.RequestLines));
    }

    [Fact]
    public async Task Http_AllowlistedHost_StillRedirectsLocallyToHttps()
    {
        // القاعدة القديمة تبقى: لا بايتات نصية عبر النفق، ولا مرور بـ Proxy النظام.
        await using var upstream = new FakeUpstreamProxy();
        await using var proxy = Proxies.Start(new FakeMux(), systemProxy: upstream.AsResolver());
        await using var client = await ProxyClient.ConnectAsync(proxy.Port);

        await client.SendAsync("GET http://allowed.example/x HTTP/1.1\r\nHost: allowed.example\r\n\r\n");
        var head = await client.ReadHeadAsync();
        Assert.Equal(307, head.Status);
        Assert.Equal("https://allowed.example/x", head.Headers["Location"]);
        Assert.Equal(0, upstream.Connections);
    }

    [Fact]
    public async Task Http_ProbePage_IsServedLocally_NotThroughTheProxy()
    {
        await using var upstream = new FakeUpstreamProxy();
        await using var proxy = Proxies.Start(new FakeMux(), systemProxy: upstream.AsResolver());
        await using var client = await ProxyClient.ConnectAsync(proxy.Port);

        await client.SendAsync("GET http://check.josour/ HTTP/1.1\r\nHost: check.josour\r\n\r\n");
        Assert.Equal(200, (await client.ReadHeadAsync()).Status);
        Assert.Equal(0, upstream.Connections);
        Assert.Equal(1, proxy.Counters.ProbeHits);
    }

    [Fact]
    public async Task Http_UpstreamDemandsAuth_407ReachesTheBrowserUntouched()
    {
        await using var upstream = new FakeUpstreamProxy { RequireAuthentication = true };
        await using var proxy = Proxies.Start(new FakeMux(), systemProxy: upstream.AsResolver());
        await using var client = await ProxyClient.ConnectAsync(proxy.Port);

        await client.SendAsync("GET http://direct.test/x HTTP/1.1\r\nHost: direct.test\r\n\r\n");
        var head = await client.ReadHeadAsync();
        Assert.Equal(407, head.Status);
        Assert.Equal("Basic realm=\"corp\"", head.Headers["Proxy-Authenticate"]);
    }

    [Fact]
    public async Task Http_ProxyAuthorization_IsForwardedUpstream_ButNeverToAnOrigin()
    {
        // مع Proxy: الترويسة موجّهة إليه فتُمرَّر. بلا Proxy: لا مُخاطَب لها فتُحذف (السلوك القائم).
        await using var upstream = new FakeUpstreamProxy();
        await using (var proxy = Proxies.Start(new FakeMux(), systemProxy: upstream.AsResolver()))
        await using (var client = await ProxyClient.ConnectAsync(proxy.Port))
        {
            await client.SendAsync("GET http://direct.test/x HTTP/1.1\r\nHost: direct.test\r\nProxy-Authorization: Basic dTpw\r\n\r\n");
            Assert.Equal(200, (await client.ReadHeadAsync()).Status);
        }
        Assert.Equal("Basic dTpw", Assert.Single(upstream.Authorizations));

        using var origin = new HttpOrigin();
        var serve = origin.ServeOnceAsync("hi");
        var resolver = new StubResolver().Map("direct.test", "127.0.0.1");
        await using (var proxy = Proxies.Start(new FakeMux(), resolver, systemProxy: NoSystemProxy.Instance))
        await using (var client = await ProxyClient.ConnectAsync(proxy.Port))
        {
            await client.SendAsync($"GET http://direct.test:{origin.Port}/x HTTP/1.1\r\nHost: direct.test:{origin.Port}\r\nProxy-Authorization: Basic dTpw\r\n\r\n");
            Assert.Equal(200, (await client.ReadHeadAsync()).Status);
        }
        await serve.WaitAsync(Timeout);
        Assert.DoesNotContain("Proxy-Authorization", origin.ReceivedHead ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    // ---------- بناء الرأس ----------

    [Fact]
    public void BuildProxyFormHead_UsesTheNormalisedHost_NotTheRawTarget()
    {
        // الهدف يُبنى من الاسم المطبَّع، فلا يمر ما كتبه المتصفح حرفيًا إلى الـ Proxy الأعلى.
        var request = HttpRequestParser.Parse(
            Encoding.ASCII.GetBytes("GET http://EXAMPLE.test/a%20b?q=1 HTTP/1.1\r\nHost: EXAMPLE.test\r\nProxy-Connection: keep-alive\r\nKeep-Alive: 5\r\nConnection: keep-alive\r\nProxy-Authorization: Basic x\r\nAccept: */*\r\n\r\n"),
            Array.Empty<byte>());
        var head = Encoding.Latin1.GetString(ConnectProxyServer.BuildProxyFormHead(request, "example.test", 80, "/a%20b?q=1"));

        Assert.StartsWith("GET http://example.test/a%20b?q=1 HTTP/1.1\r\n", head, StringComparison.Ordinal);
        Assert.DoesNotContain("Proxy-Connection", head, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Keep-Alive: 5", head, StringComparison.Ordinal);
        Assert.Contains("Proxy-Authorization: Basic x\r\n", head, StringComparison.Ordinal);
        Assert.Contains("Accept: */*\r\n", head, StringComparison.Ordinal);
        Assert.EndsWith("Connection: close\r\n\r\n", head, StringComparison.Ordinal);
        // ترويسة Connection واحدة فقط: أصل المتصفح حُذف وحلّ محله close.
        Assert.Equal(1, head.Split("\r\nConnection: ").Length - 1);
    }

    [Fact]
    public void BuildProxyFormHead_EmptyPathBecomesRoot()
    {
        var request = HttpRequestParser.Parse(
            Encoding.ASCII.GetBytes("GET http://example.test HTTP/1.1\r\nHost: example.test\r\n\r\n"),
            Array.Empty<byte>());
        var head = Encoding.Latin1.GetString(ConnectProxyServer.BuildProxyFormHead(request, "example.test", 80, string.Empty));
        Assert.StartsWith("GET http://example.test/ HTTP/1.1\r\n", head, StringComparison.Ordinal);
    }
}
