using System.Net.Sockets;
using System.Text;
using Josour.Core.Allowlist;
using Josour.Tunnel.Mux;

namespace Josour.Proxy.Tests;

/// <summary>
/// The second review round of the local proxy:
/// a reply that would let the browser reuse the proxy connection for another site gets <c>Connection: close</c>;
/// a destination meant for the host never goes direct for want of a tunnel; a name that normalises to an address literal is refused;
/// the request-target reaches the origin byte for byte; and the parser's framing rules.
/// </summary>
public class ProxyReuseAndRoutingTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private static readonly int[] DefaultPorts = { 80, 443 };

    // ---------- Connection reuse ----------

    [Fact]
    public async Task Http_Direct_AKeepAliveReply_IsRewrittenToConnectionClose()
    {
        using var origin = new LocalOrigin();
        var serve = Task.Run(async () =>
        {
            using var socket = await origin.AcceptAsync();
            using var stream = new NetworkStream(socket, true);
            _ = await HttpRequestParser.ReadAsync(stream, CancellationToken.None);
            // A hostile origin: it promises reuse and keeps the socket open, hoping the browser sends another site's request down it.
            await stream.WriteAsync(Encoding.ASCII.GetBytes(
                "HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: keep-alive, X-Hop\r\nKeep-Alive: timeout=60\r\nX-Hop: 1\r\nProxy-Connection: keep-alive\r\nX-Kept: yes\r\n\r\nok"));
            var buffer = new byte[1024];
            try { while (await stream.ReadAsync(buffer) > 0) { } } catch { }
        });
        var resolver = new StubResolver().Map("direct.test", "127.0.0.1");
        await using var proxy = Proxies.Start(new FakeMux(), resolver);
        await using var client = await ProxyClient.ConnectAsync(proxy.Port);

        await client.SendAsync($"GET http://direct.test:{origin.Port}/ HTTP/1.1\r\nHost: direct.test:{origin.Port}\r\nProxy-Connection: keep-alive\r\n\r\n");
        var head = await client.ReadHeadAsync();

        Assert.Equal(200, head.Status);
        Assert.Equal("close", head.Headers["Connection"]);
        Assert.Equal("close", head.Headers["Proxy-Connection"]);
        Assert.False(head.Headers.ContainsKey("Keep-Alive"));
        Assert.False(head.Headers.ContainsKey("X-Hop"));        // named in Connection: hop-by-hop
        Assert.Equal("yes", head.Headers["X-Kept"]);
        Assert.Equal("ok", Encoding.ASCII.GetString(await client.ReadBodyAsync(head)));
        client.Socket.Shutdown(SocketShutdown.Both);
        await serve.WaitAsync(Timeout);
    }

    [Fact]
    public async Task Http_Tunnel_AKeepAliveReply_IsRewrittenToConnectionClose_AndInterimRepliesPassThrough()
    {
        var (near, far) = await TcpPair.CreateAsync();
        using var _far = far;
        var mux = new FakeMux { OnOpen = (_, _, _) => Task.FromResult(MuxOpenResult.Ok(near)) };
        await using var proxy = Proxies.Start(mux);
        await using var client = await ProxyClient.ConnectAsync(proxy.Port);

        await client.SendAsync("POST http://allowed.example/upload HTTP/1.1\r\nHost: allowed.example\r\nContent-Length: 0\r\nExpect: 100-continue\r\n\r\n");
        using (var farStream = new NetworkStream(far, false))
        {
            _ = await HttpRequestParser.ReadAsync(farStream, CancellationToken.None).WaitAsync(Timeout);
            await farStream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 100 Continue\r\n\r\nHTTP/1.1 201 Created\r\nContent-Length: 0\r\n\r\n"));
        }
        far.Shutdown(SocketShutdown.Send);

        var text = Encoding.ASCII.GetString(await client.ReadToEndAsync());
        Assert.StartsWith("HTTP/1.1 100 Continue\r\n\r\nHTTP/1.1 201 Created\r\n", text);
        Assert.Contains("\r\nConnection: close\r\nProxy-Connection: close\r\n\r\n", text);
        Assert.Equal(1, System.Text.RegularExpressions.Regex.Matches(text, "\r\nConnection: close\r\n").Count);
    }

    [Fact]
    public async Task Http_Direct_AMalformedReply_Is502()
    {
        using var origin = new LocalOrigin();
        var serve = Task.Run(async () =>
        {
            using var socket = await origin.AcceptAsync();
            using var stream = new NetworkStream(socket, true);
            _ = await HttpRequestParser.ReadAsync(stream, CancellationToken.None);
            await stream.WriteAsync(Encoding.ASCII.GetBytes("SSH-2.0-OpenSSH\r\n\r\n"));
            socket.Shutdown(SocketShutdown.Send);
        });
        var resolver = new StubResolver().Map("direct.test", "127.0.0.1");
        await using var proxy = Proxies.Start(new FakeMux(), resolver);
        await using var client = await ProxyClient.ConnectAsync(proxy.Port);

        await client.SendAsync($"GET http://direct.test:{origin.Port}/ HTTP/1.1\r\nHost: direct.test:{origin.Port}\r\n\r\n");
        Assert.Equal(502, (await client.ReadHeadAsync()).Status);
        await serve.WaitAsync(Timeout);
    }

    [Theory]
    [InlineData("HTTP/1.1 200 OK\n\nbody", 17)]                       // a bare-LF origin
    [InlineData("HTTP/1.1 200 OK\r\nA: b\r\n\r\nbody", 25)]
    [InlineData("HTTP/1.1 200 OK\r\nA: b\r\n", -1)]
    public void FindHeadEnd(string data, int expected)
    {
        Assert.Equal(expected, HttpResponseRelay.FindHeadEnd(Encoding.ASCII.GetBytes(data)));
    }

    // ---------- No tunnel means no tunnelled destination, never a direct one ----------

    [Fact]
    public async Task Connect_TunnelRouteWithNoMux_Is502_AndNothingLeavesDirectly()
    {
        var resolver = new StubResolver().Map("allowed.example", "127.0.0.1");
        await using var proxy = Proxies.Start(mux: null, resolver: resolver);
        await using var client = await ProxyClient.ConnectAsync(proxy.Port);

        await client.SendAsync("CONNECT allowed.example:443 HTTP/1.1\r\nHost: allowed.example:443\r\n\r\n");
        Assert.Equal(502, (await client.ReadHeadAsync()).Status);
        Assert.Equal(0, resolver.Calls);
        Assert.Equal(0, proxy.Counters.DirectConnects);
    }

    [Fact]
    public async Task Http_TunnelRouteWithNoMux_Is502_AndNothingLeavesDirectly()
    {
        var resolver = new StubResolver().Map("allowed.example", "127.0.0.1");
        await using var proxy = Proxies.Start(mux: null, resolver: resolver);
        await using var client = await ProxyClient.ConnectAsync(proxy.Port);

        await client.SendAsync("GET http://allowed.example/ HTTP/1.1\r\nHost: allowed.example\r\n\r\n");
        Assert.Equal(502, (await client.ReadHeadAsync()).Status);
        Assert.Equal(0, resolver.Calls);
        Assert.Equal(0, proxy.Counters.DirectHttpRequests);
    }

    [Fact]
    public void DirectPath_OnlyForAnEnforcedList()
    {
        Assert.True(ProxyRouter.DirectAllowedFor(AllowlistMatcher.Parse(1, new[] { "a.example" })));
        Assert.False(ProxyRouter.DirectAllowedFor(new AllowAllAllowlist()));
        Assert.False(ProxyRouter.DirectAllowedFor(DenyAllAllowlist.Instance)); // no list supplied: fail closed
        // A session built without saying which list applies gets that fail-closed default.
        Assert.Same(DenyAllAllowlist.Instance, new Josour.Tunnel.TunnelSessionOptions().Allowlist);
    }

    [Fact]
    public async Task NoListSupplied_RefusesEverything_AndNeverGoesDirect()
    {
        var resolver = new StubResolver().Map("any.example", "127.0.0.1");
        await using var proxy = Proxies.Start(new FakeMux(), resolver, allowlist: DenyAllAllowlist.Instance);
        await using var client = await ProxyClient.ConnectAsync(proxy.Port);

        await client.SendAsync("CONNECT any.example:443 HTTP/1.1\r\n\r\n");
        Assert.Equal(403, (await client.ReadHeadAsync()).Status);
        Assert.Equal(0, resolver.Calls);
    }

    // ---------- Literals after normalisation ----------

    [Theory]
    [InlineData("8.8.8.8.")]
    [InlineData("10.0.0.1.")]
    [InlineData("0x7f.0.0.1.")]
    [InlineData("１２７.０.０.１")]
    public void Router_ANameThatNormalisesToALiteral_IsRefused(string host)
    {
        foreach (var directAllowed in new[] { true, false })
        {
            var route = ProxyRouter.Decide(host, 443, new AllowAllAllowlist(), DefaultPorts, directAllowed);
            Assert.Equal(RouteKind.Reject, route.Kind);
            Assert.Equal("ip_literal", route.Reason);
        }
    }

    [Fact]
    public async Task Connect_TrailingDotLiteral_Is403_WithNoOpen()
    {
        var mux = new FakeMux();
        await using var proxy = Proxies.Start(mux, allowlist: new AllowAllAllowlist());
        await using var client = await ProxyClient.ConnectAsync(proxy.Port);
        await client.SendAsync("CONNECT 8.8.8.8.:443 HTTP/1.1\r\n\r\n");
        Assert.Equal(403, (await client.ReadHeadAsync()).Status);
        Assert.Empty(mux.Opens);
    }

    // ---------- The request-target, byte for byte ----------

    [Theory]
    [InlineData("http://a.example/x/%2e%2e/y?q=%41%2F", "/x/%2e%2e/y?q=%41%2F")]
    [InlineData("http://a.example/a/../b", "/a/../b")]
    [InlineData("http://a.example/p?a=%zz", "/p?a=%zz")]
    [InlineData("http://a.example/x#frag", "/x")]
    [InlineData("http://a.example?q=1", "/?q=1")]
    [InlineData("http://a.example", "/")]
    [InlineData("http://user@a.example:8080/p", "/p")]
    public void TryParseHttpUri_KeepsTheRawPathAndQuery(string target, string expected)
    {
        Assert.True(HttpRequestParser.TryParseHttpUri(target, out var host, out _, out var path));
        Assert.Equal("a.example", host);
        Assert.Equal(expected, path);
    }

    [Fact]
    public void TryParseHttpUri_RefusesABackslashBeforeThePath()
    {
        Assert.False(HttpRequestParser.TryParseHttpUri("http://a.example\\@evil.example/", out _, out _, out _));
    }

    [Fact]
    public async Task Http_Tunnel_TheOriginReceivesTheRawTarget()
    {
        var (near, far) = await TcpPair.CreateAsync();
        using var _far = far;
        var mux = new FakeMux { OnOpen = (_, _, _) => Task.FromResult(MuxOpenResult.Ok(near)) };
        await using var proxy = Proxies.Start(mux);
        await using var client = await ProxyClient.ConnectAsync(proxy.Port);

        await client.SendAsync("GET http://allowed.example/sig/%2e%2e/x?s=%41b HTTP/1.1\r\nHost: allowed.example\r\n\r\n");
        using var farStream = new NetworkStream(far, false);
        var request = await HttpRequestParser.ReadAsync(farStream, CancellationToken.None).WaitAsync(Timeout);
        Assert.Equal("/sig/%2e%2e/x?s=%41b", request!.Target);
    }

    // ---------- Framing ----------

    [Theory]
    [InlineData("Content-Length: 5\r\nContent-Length: 6\r\n")]
    [InlineData("Content-Length: 5, 5\r\n")]
    [InlineData("Content-Length: +5\r\n")]
    [InlineData("Content-Length: \r\n")]
    [InlineData("Transfer-Encoding: chunked\r\nTransfer-Encoding: chunked\r\n")]
    [InlineData("Transfer-Encoding: gzip\r\n")]
    [InlineData("Transfer-Encoding: chunked, gzip\r\n")]
    [InlineData("Bad(Name): x\r\n")]
    [InlineData("Bad\"Name: x\r\n")]
    [InlineData("Bad/Name: x\r\n")]
    public void Parser_RefusesAmbiguousFramingAndNonTokenNames(string headers)
    {
        var head = Encoding.Latin1.GetBytes($"POST http://a.example/ HTTP/1.1\r\nHost: a.example\r\n{headers}\r\n");
        Assert.Throws<HttpParseException>(() => HttpRequestParser.Parse(head, Array.Empty<byte>()));
    }

    [Theory]
    [InlineData("Content-Length: 5\r\n")]
    [InlineData("Content-Length: 5\r\nContent-Length: 5\r\n")]
    [InlineData("Transfer-Encoding: gzip, chunked\r\n")]
    [InlineData("Transfer-Encoding: Chunked\r\n")]
    [InlineData("X-Custom_Header.v2: x\r\n")]
    public void Parser_AcceptsUnambiguousFraming(string headers)
    {
        var head = Encoding.Latin1.GetBytes($"POST http://a.example/ HTTP/1.1\r\nHost: a.example\r\n{headers}\r\n");
        Assert.NotNull(HttpRequestParser.Parse(head, Array.Empty<byte>()));
    }
}
