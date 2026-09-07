using System.Text;
using Josour.Core.Allowlist;
using Josour.Tunnel.Mux;

namespace Josour.Proxy.Tests;

public class HttpRequestParserTests
{
    [Theory]
    [InlineData("example.com:443", 443, "example.com", 443)]
    [InlineData("example.com", 443, "example.com", 443)]
    [InlineData("example.com:8443", 443, "example.com", 8443)]
    [InlineData("[2001:db8::1]:8443", 443, "2001:db8::1", 8443)]
    [InlineData("[::1]", 80, "::1", 80)]
    [InlineData("[::ffff:10.0.0.1]:443", 443, "::ffff:10.0.0.1", 443)]
    public void TryParseAuthority_Ok(string authority, int defaultPort, string host, int port)
    {
        Assert.True(HttpRequestParser.TryParseAuthority(authority, defaultPort, out var h, out var p));
        Assert.Equal(host, h);
        Assert.Equal(port, p);
    }

    [Theory]
    [InlineData("")]
    [InlineData(":443")]
    [InlineData("example.com:")]
    [InlineData("example.com:0")]
    [InlineData("example.com:65536")]
    [InlineData("example.com:abc")]
    [InlineData("2001:db8::1:443")]
    [InlineData("[2001:db8::1")]
    [InlineData("[]:443")]
    [InlineData("[::1]x")]
    [InlineData("example.com/path")]
    public void TryParseAuthority_Rejects(string authority)
    {
        Assert.False(HttpRequestParser.TryParseAuthority(authority, 443, out _, out _));
    }

    [Theory]
    [InlineData("http://example.com/a/b?c=1", "example.com", 80, "/a/b?c=1")]
    [InlineData("http://example.com", "example.com", 80, "/")]
    [InlineData("http://example.com:8080/", "example.com", 8080, "/")]
    [InlineData("http://[2001:db8::1]:81/x", "2001:db8::1", 81, "/x")]
    [InlineData("HTTP://EXAMPLE.com/", "example.com", 80, "/")]
    public void TryParseHttpUri_Ok(string target, string host, int port, string path)
    {
        Assert.True(HttpRequestParser.TryParseHttpUri(target, out var h, out var p, out var pq));
        Assert.Equal(host, h);
        Assert.Equal(port, p);
        Assert.Equal(path, pq);
    }

    [Theory]
    [InlineData("https://example.com/")]
    [InlineData("/relative")]
    [InlineData("ws://example.com/")]
    [InlineData("example.com")]
    public void TryParseHttpUri_Rejects(string target)
    {
        Assert.False(HttpRequestParser.TryParseHttpUri(target, out _, out _, out _));
    }

    [Fact]
    public async Task ReadAsync_PreservesRemainderAfterHead_AndParsesHeaders()
    {
        var bytes = Encoding.ASCII.GetBytes("CONNECT a.example:443 HTTP/1.1\r\nHost: a.example:443\r\nProxy-Connection:  keep-alive \r\n\r\n\x16\x03\x01tls");
        using var ms = new MemoryStream(bytes);
        var request = await HttpRequestParser.ReadAsync(ms, CancellationToken.None);
        Assert.NotNull(request);
        Assert.True(request!.IsConnect);
        Assert.Equal("a.example:443", request.Target);
        Assert.Equal("HTTP/1.1", request.Version);
        Assert.Equal("keep-alive", request.Header("proxy-connection"));
        Assert.Equal("a.example:443", request.Header("Host"));
        Assert.Equal(Encoding.ASCII.GetBytes("\x16\x03\x01tls"), request.Remainder);
    }

    [Fact]
    public async Task ReadAsync_HeadSplitAcrossReads()
    {
        var (a, b) = await LoopbackPair();
        await using var _a = a;
        await using var _b = b;
        var task = HttpRequestParser.ReadAsync(b, CancellationToken.None);
        await a.WriteAsync(Encoding.ASCII.GetBytes("GET http://x.example/ HTTP/1.1\r\nHost: x.example\r\n\r"));
        await Task.Delay(50);
        await a.WriteAsync(Encoding.ASCII.GetBytes("\nbody"));
        var request = await task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("GET", request!.Method);
        Assert.Equal("body", Encoding.ASCII.GetString(request.Remainder));
    }

    [Fact]
    public async Task ReadAsync_EmptyStream_ReturnsNull()
    {
        using var ms = new MemoryStream();
        Assert.Null(await HttpRequestParser.ReadAsync(ms, CancellationToken.None));
    }

    [Fact]
    public async Task ReadAsync_TruncatedHead_Throws()
    {
        using var ms = new MemoryStream(Encoding.ASCII.GetBytes("GET / HTTP/1.1\r\nHost: x"));
        await Assert.ThrowsAsync<HttpParseException>(() => HttpRequestParser.ReadAsync(ms, CancellationToken.None));
    }

    [Fact]
    public async Task ReadAsync_HeadTooLarge_Throws()
    {
        var big = "GET / HTTP/1.1\r\nX: " + new string('a', HttpRequestParser.MaxHeadBytes) + "\r\n\r\n";
        using var ms = new MemoryStream(Encoding.ASCII.GetBytes(big));
        await Assert.ThrowsAsync<HttpParseException>(() => HttpRequestParser.ReadAsync(ms, CancellationToken.None));
    }

    [Theory]
    [InlineData("GET\r\n\r\n")]
    [InlineData("GET / HTTP/2\r\n\r\n")]
    [InlineData("GET / HTTP/1.1\r\nno-colon\r\n\r\n")]
    [InlineData("GET / HTTP/1.1\r\n: empty\r\n\r\n")]
    public void Parse_Malformed_Throws(string head)
    {
        Assert.Throws<HttpParseException>(() => HttpRequestParser.Parse(Encoding.ASCII.GetBytes(head), Array.Empty<byte>()));
    }

    [Fact]
    public void BuildOriginFormHead_RewritesAndDropsHopHeaders()
    {
        var request = HttpRequestParser.Parse(Encoding.ASCII.GetBytes("POST http://d.example/api HTTP/1.1\r\nHost: d.example\r\nProxy-Connection: keep-alive\r\nProxy-Authorization: x\r\nConnection: keep-alive\r\nKeep-Alive: 5\r\nContent-Length: 2\r\n\r\n"), Array.Empty<byte>());
        var head = Encoding.Latin1.GetString(ConnectProxyServer.BuildOriginFormHead(request, "/api"));
        Assert.Equal("POST /api HTTP/1.1\r\nHost: d.example\r\nContent-Length: 2\r\nConnection: close\r\n\r\n", head);
    }

    [Theory]
    [InlineData(OpenFailReason.NotAllowed, 403)]
    [InlineData(OpenFailReason.PrivateIp, 403)]
    [InlineData(OpenFailReason.PortNotAllowed, 403)]
    [InlineData(OpenFailReason.IpLiteral, 403)]
    [InlineData(OpenFailReason.DnsFailed, 502)]
    [InlineData(OpenFailReason.ConnectFailed, 502)]
    [InlineData(OpenFailReason.Limit, 503)]
    public void StatusForOpenFail_Table(OpenFailReason reason, int status)
    {
        Assert.Equal(status, ProxyRouter.StatusForOpenFail(reason));
    }

    [Theory]
    [InlineData("allowed.example", 443, RouteKind.Tunnel)]
    [InlineData("Sub.Allowed.Example.", 80, RouteKind.Tunnel)]
    [InlineData("allowed.example", 8080, RouteKind.Direct)]
    [InlineData("other.example", 443, RouteKind.Direct)]
    [InlineData("10.0.0.1", 443, RouteKind.Reject)]
    [InlineData("[::1]", 443, RouteKind.Reject)]
    [InlineData("1.1.1.1", 443, RouteKind.Reject)]
    [InlineData("localhost", 443, RouteKind.Reject)]
    [InlineData("a.localhost", 443, RouteKind.Reject)]
    [InlineData("", 443, RouteKind.Reject)]
    [InlineData("allowed.example", 0, RouteKind.Reject)]
    public void Router_Decide_Table(string host, int port, RouteKind kind)
    {
        var allowlist = AllowlistMatcher.Parse(1, Proxies.DefaultEntries);
        Assert.Equal(kind, ProxyRouter.Decide(host, port, allowlist, new[] { 80, 443 }).Kind);
    }

    [Fact]
    public void WindowsPidChecker_PortByteOrder()
    {
        // 0x01BB = 443 بترتيب الشبكة في أدنى 16 بت: البايت الأدنى أولًا
        Assert.Equal(443, TcpTableFormat.PortFromDword(0x0000BB01));
        Assert.Equal(1, TcpTableFormat.PortFromDword(0x00000100));
    }

    private static async Task<(Stream, Stream)> LoopbackPair()
    {
        var (near, far) = await TcpPair.CreateAsync();
        return (near, new System.Net.Sockets.NetworkStream(far, true));
    }
}
