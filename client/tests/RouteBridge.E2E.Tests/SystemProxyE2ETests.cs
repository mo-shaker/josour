using System.Net;
using System.Net.Sockets;
using System.Text;
using RouteBridge.Core.Net;
using RouteBridge.Proxy.Tests;

namespace RouteBridge.E2E.Tests;

/// <summary>
/// المكدس كاملًا مع Proxy إجباري على شبكة الضيف (الخطة 8.5): القسمة تبقى كما هي —
/// المسموح به يخرج من عند <b>المضيف</b> عبر النفق، وغير المسموح به يخرج من عند <b>الضيف</b> عبر Proxy شبكته.
/// </summary>
public class SystemProxyE2ETests : IAsyncLifetime
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);
    private FakeUpstreamProxy _upstream = null!;
    private InProcessTunnelPair _pair = null!;

    public async Task InitializeAsync()
    {
        _upstream = new FakeUpstreamProxy();
        _pair = await InProcessTunnelPair.CreateAsync(guestSystemProxy: _upstream.AsResolver());
    }

    public async Task DisposeAsync()
    {
        await _pair.DisposeAsync();
        await _upstream.DisposeAsync();
    }

    [Fact]
    public async Task Allowlisted_StillGoesThroughTheTunnel_NotTheSystemProxy()
    {
        var serve = _pair.Origin.ServeOnceAsync("tunnelled body");
        using var client = await _pair.ConnectToProxyAsync();
        var stream = client.GetStream();

        await stream.WriteAsync(E2EWait.Ascii($"CONNECT site.test:{_pair.OriginPort} HTTP/1.1\r\nHost: site.test:{_pair.OriginPort}\r\n\r\n"));
        Assert.Equal(200, (await Http.ReadHeadAsync(stream)).Status);
        await stream.WriteAsync(E2EWait.Ascii("GET /via-tunnel HTTP/1.1\r\nHost: site.test\r\n\r\n"));
        var head = await Http.ReadHeadAsync(stream);
        Assert.Equal(200, head.Status);
        Assert.Equal("tunnelled body", Encoding.UTF8.GetString(await Http.ReadBodyAsync(stream, head)));
        client.Client.Shutdown(SocketShutdown.Send);
        await serve.WaitAsync(Timeout);

        Assert.Contains("site.test", _pair.Host.DomainsSeen);
        Assert.Equal(1, _pair.ProxyServer.Counters.Tunneled);
        Assert.Equal(0, _upstream.Connections);
        Assert.Equal(0, _pair.ProxyServer.Counters.ViaSystemProxy);
    }

    [Fact]
    public async Task NotAllowlistedConnect_GoesThroughTheGuestSystemProxy_NeverTheHost()
    {
        using var client = await _pair.ConnectToProxyAsync();
        var stream = client.GetStream();
        await stream.WriteAsync(E2EWait.Ascii($"CONNECT direct.test:{_pair.OriginPort} HTTP/1.1\r\nHost: direct.test:{_pair.OriginPort}\r\n\r\n"));
        Assert.Equal(200, (await Http.ReadHeadAsync(stream)).Status);

        Assert.Equal($"CONNECT direct.test:{_pair.OriginPort} HTTP/1.1", Assert.Single(_upstream.RequestLines));
        Assert.Equal(1, _pair.ProxyServer.Counters.ViaSystemProxy);
        Assert.Empty(_pair.Host.DomainsSeen);
        Assert.Equal(0, _pair.HostHandler.OpensOk);
    }

    [Fact]
    public async Task NotAllowlistedHttp_GoesThroughTheGuestSystemProxyInAbsoluteUriForm()
    {
        _upstream.HttpBody = "served by the corporate proxy";
        using var http = _pair.NewProxiedClient();
        var response = await http.GetAsync("http://direct.test/plain").WaitAsync(Timeout);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("served by the corporate proxy", await response.Content.ReadAsStringAsync());
        Assert.Equal("GET http://direct.test/plain HTTP/1.1", Assert.Single(_upstream.RequestLines));
        Assert.Equal(1, _pair.ProxyServer.Counters.ViaSystemProxy);
        Assert.Empty(_pair.Host.DomainsSeen);
    }
}
