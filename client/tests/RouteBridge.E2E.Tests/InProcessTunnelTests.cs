using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using RouteBridge.Core.Allowlist;
using RouteBridge.Core.Net;
using RouteBridge.Core.Tunnel;
using RouteBridge.Egress;
using RouteBridge.Proxy;
using RouteBridge.Tunnel;
using RouteBridge.Tunnel.Certificates;
using RouteBridge.Tunnel.Mux;
using RouteBridge.Tunnel.Transport;

namespace RouteBridge.E2E.Tests;

/// <summary>
/// المسار كاملًا داخل العملية: SymmetricConnector على loopback → NerdbankMux في الطرفين → OpenHandler/EgressPolicy على المضيف →
/// ConnectProxyServer على المستخدم → متصفح وهمي (مقبس خام + HttpClient بـ Proxy). المحلل يربط site.test/direct.test بـ 127.0.0.1
/// وسياسة الحظر تُستبدل للسماح بـ loopback في هذا الاختبار فقط.
/// </summary>
public class InProcessTunnelTests : IAsyncLifetime
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);
    private const string PeerPublicIp = "203.0.113.7";

    private SessionCertificate? _hostCert, _guestCert;
    private TunnelListener? _hostListener, _guestListener;
    private NerdbankMux? _hostMux, _guestMux;
    private HttpOrigin? _origin;
    private OpenHandler? _handler;
    private ConnectProxyServer? _proxy;
    private string _tlsVersion = "";

    public async Task InitializeAsync()
    {
        _origin = new HttpOrigin();
        var sessionId = Guid.NewGuid();
        var secret = RandomNumberGenerator.GetBytes(32);
        var expires = DateTimeOffset.UtcNow.AddMinutes(30);
        var hostMaterial = new SessionMaterial(sessionId, TunnelRole.Host, secret, expires, true, "198.51.100.2");
        var guestMaterial = new SessionMaterial(sessionId, TunnelRole.Guest, secret, expires, true, PeerPublicIp);
        _hostCert = SessionCertificate.Create(expires);
        _guestCert = SessionCertificate.Create(expires);
        _hostListener = new TunnelListener(0, IPAddress.Loopback);
        _guestListener = new TunnelListener(0, IPAddress.Loopback);
        var hostCandidates = new[] { new CandidateEndpoint(CandidateType.Lan, "127.0.0.1", _hostListener.Port) };
        var guestCandidates = new[] { new CandidateEndpoint(CandidateType.Lan, "127.0.0.1", _guestListener.Port) };
        var hostConnector = new SymmetricConnector(hostMaterial, _hostCert, _hostListener, new DirectTransport(), hostCandidates);
        var guestConnector = new SymmetricConnector(guestMaterial, _guestCert, _guestListener, new DirectTransport(), guestCandidates);

        var hostTask = hostConnector.ConnectAsync(new PeerEndpointInfo(_guestCert.FingerprintHex, guestCandidates), Timeout, CancellationToken.None);
        var guestTask = guestConnector.ConnectAsync(new PeerEndpointInfo(_hostCert.FingerprintHex, hostCandidates), Timeout, CancellationToken.None);
        var host = await hostTask;
        var guest = await guestTask;
        if (!host.Result.Connected || !guest.Result.Connected) throw new InvalidOperationException("symmetric connect failed: " + host.Result.FailureReason + "/" + guest.Result.FailureReason);
        _tlsVersion = host.Result.TlsVersion!;

        // المضيف: Mux + سياسة الخروج (site.test مسموح على منفذ الأصل فقط)
        _hostMux = NerdbankMux.Create(host.Connection!.Stream, TunnelRole.Host);
        var allowlist = AllowlistMatcher.Parse(1, new[] { $"site.test:{_origin.Port}" });
        var policy = new EgressPolicy(allowlist, new EgressPolicyOptions
        {
            Resolver = new StubResolver().Map("site.test", "127.0.0.1"),
            AddressBlocker = a => !IPAddress.IsLoopback(a) && IpRangePolicy.IsBlocked(a), // loopback مسموح هنا فقط
        });
        _handler = new OpenHandler(policy);
        _handler.Attach(_hostMux);

        // المستخدم: Mux + Proxy (القائمة نفسها؛ direct.test غير مسموح → مباشر)
        _guestMux = NerdbankMux.Create(guest.Connection!.Stream, TunnelRole.Guest);
        _proxy = new ConnectProxyServer(new ConnectProxyOptions
        {
            Allowlist = allowlist,
            Mux = _guestMux,
            PeerPublicIp = PeerPublicIp,
            Resolver = new StubResolver().Map("direct.test", "127.0.0.1").Map("site.test", "127.0.0.1"),
        });
        _proxy.Start();
    }

    public async Task DisposeAsync()
    {
        if (_proxy is not null) await _proxy.DisposeAsync();
        if (_guestMux is not null) await _guestMux.DisposeAsync();
        if (_hostMux is not null) await _hostMux.DisposeAsync();
        if (_hostListener is not null) await _hostListener.DisposeAsync();
        if (_guestListener is not null) await _guestListener.DisposeAsync();
        _hostCert?.Dispose();
        _guestCert?.Dispose();
        _origin?.Dispose();
    }

    [Fact]
    public async Task Connect_Allowlisted_FlowsThroughTunnel_CountsBytes_CollectsDomain()
    {
        Assert.Contains(_tlsVersion, new[] { "1.2", "1.3" });
        var serve = _origin!.ServeOnceAsync("tunnelled body");
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, _proxy!.Port);
        var stream = client.GetStream();

        await stream.WriteAsync(Encoding.ASCII.GetBytes($"CONNECT site.test:{_origin.Port} HTTP/1.1\r\nHost: site.test:{_origin.Port}\r\n\r\n"));
        var connectHead = await Http.ReadHeadAsync(stream);
        Assert.Equal(200, connectHead.Status);
        Assert.Empty(connectHead.Remainder);

        var request = Encoding.ASCII.GetBytes("GET /via-tunnel HTTP/1.1\r\nHost: site.test\r\n\r\n");
        await stream.WriteAsync(request);
        var head = await Http.ReadHeadAsync(stream);
        Assert.Equal(200, head.Status);
        var body = await Http.ReadBodyAsync(stream, head);
        Assert.Equal("tunnelled body", Encoding.UTF8.GetString(body));

        // EOF من الأصل ينتقل عبر النفق إلى المتصفح (إغلاق نصفي)
        var one = new byte[1];
        Assert.Equal(0, await stream.ReadAsync(one).AsTask().WaitAsync(Timeout));
        // المتصفح يغلق جانبه → ينتقل عبر النفق كـ Shutdown(Send) نحو الأصل فيرى نهاية الاتصال
        client.Client.Shutdown(SocketShutdown.Send);
        await serve.WaitAsync(Timeout);
        Assert.StartsWith("GET /via-tunnel HTTP/1.1", _origin.ReceivedHead);

        Assert.Equal(request.Length, _handler!.Counter.Up);
        Assert.True(_handler.Counter.Down >= body.Length, $"down={_handler.Counter.Down}");
        Assert.Contains("site.test", _handler.Domains.Snapshot());
        Assert.Equal(1, _handler.OpensOk);
        Assert.True(_guestMux!.Stats.BytesUp > request.Length);
        Assert.True(_hostMux!.Stats.BytesUp > body.Length);
        Assert.Equal(1, _proxy.Counters.Tunneled);
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (_handler.Limiter.Active > 0 && DateTime.UtcNow < deadline) await Task.Delay(20);
        Assert.Equal(0, _handler.Limiter.Active);
    }

    [Fact]
    public async Task Http_NotAllowlisted_GoesDirect_ViaHttpClientProxy()
    {
        var serve = _origin!.ServeOnceAsync("direct body");
        using var http = NewProxiedClient();
        var response = await http.GetAsync($"http://direct.test:{_origin.Port}/direct").WaitAsync(Timeout);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("direct body", await response.Content.ReadAsStringAsync());
        await serve.WaitAsync(Timeout);
        Assert.StartsWith("GET /direct HTTP/1.1", _origin.ReceivedHead);
        Assert.Contains("Connection: close", _origin.ReceivedHead);
        Assert.DoesNotContain("direct.test", _handler!.Domains.Snapshot());
        Assert.Equal(0, _handler.OpensOk);
        Assert.Equal(1, _proxy!.Counters.DirectHttpRequests);
    }

    [Fact]
    public async Task ProbePage_ThroughHttpClientProxy_RaisesProbeHit()
    {
        DateTimeOffset? hit = null;
        _proxy!.ProbeHit += t => hit = t;
        using var http = NewProxiedClient();
        var html = await http.GetStringAsync("http://check.routebridge/").WaitAsync(Timeout);
        Assert.Contains($"Tunnel active. Sites will see: {PeerPublicIp}", html);
        Assert.NotNull(hit);
        Assert.NotNull(_proxy.FirstProbeHitAt);
    }

    [Fact]
    public async Task Connect_AllowlistedHost_WrongPort_HostRejects_403()
    {
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, _proxy!.Port);
        var stream = client.GetStream();
        // المنفذ 443 ليس ضمن قيد المدخل site.test:<port> → المسار يعتبره غير مسموح ويحاول مباشرة → لا خادم على 443 → 502
        await stream.WriteAsync(Encoding.ASCII.GetBytes("CONNECT site.test:443 HTTP/1.1\r\n\r\n"));
        var head = await Http.ReadHeadAsync(stream);
        Assert.Equal(502, head.Status);
    }

    [Fact]
    public async Task Connect_AllowlistSkew_HostSaysNotAllowed_FallsBackToDirect()
    {
        // المضيف بقائمة أحدث لا تحوي site.test: OPEN_FAIL(not_allowed) → المستخدم يسقط إلى المباشر
        var strict = new EgressPolicy(AllowlistMatcher.Parse(2, new[] { "other.example" }), new EgressPolicyOptions { Resolver = new StubResolver() });
        var strictHandler = new OpenHandler(strict);
        strictHandler.Attach(_hostMux!);
        try
        {
            var serve = _origin!.ServeOnceAsync("fallback body");
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, _proxy!.Port);
            var stream = client.GetStream();
            await stream.WriteAsync(Encoding.ASCII.GetBytes($"CONNECT site.test:{_origin.Port} HTTP/1.1\r\n\r\n"));
            Assert.Equal(200, (await Http.ReadHeadAsync(stream)).Status);
            await stream.WriteAsync(Encoding.ASCII.GetBytes("GET /fb HTTP/1.1\r\nHost: site.test\r\n\r\n"));
            var head = await Http.ReadHeadAsync(stream);
            Assert.Equal("fallback body", Encoding.UTF8.GetString(await Http.ReadBodyAsync(stream, head)));
            client.Client.Shutdown(SocketShutdown.Send);
            await serve.WaitAsync(Timeout);
            Assert.Equal(1, strictHandler.OpensFailed);
            Assert.Equal(1, _proxy.Counters.DirectConnects);
        }
        finally
        {
            _handler!.Attach(_hostMux!);
        }
    }

    [Fact]
    public async Task Connect_PrivateIpLiteral_RejectedLocally_NeverReachesHost()
    {
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, _proxy!.Port);
        var stream = client.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes("CONNECT 192.168.1.1:443 HTTP/1.1\r\n\r\n"));
        Assert.Equal(403, (await Http.ReadHeadAsync(stream)).Status);
        Assert.Equal(0, _handler!.OpensOk + _handler.OpensFailed);
    }

    [Fact]
    public async Task Tunnel_Liveness_PingWorks_BothWays()
    {
        Assert.InRange((await _guestMux!.PingAsync(CancellationToken.None)).TotalMilliseconds, 0, 5000);
        Assert.InRange((await _hostMux!.PingAsync(CancellationToken.None)).TotalMilliseconds, 0, 5000);
    }

    private HttpClient NewProxiedClient()
    {
        var handler = new HttpClientHandler { Proxy = new WebProxy($"http://127.0.0.1:{_proxy!.Port}"), UseProxy = true };
        return new HttpClient(handler) { Timeout = Timeout };
    }
}

internal sealed class StubResolver : IHostResolver
{
    private readonly Dictionary<string, IPAddress[]> _map = new(StringComparer.Ordinal);

    public StubResolver Map(string host, params string[] addresses)
    {
        _map[host] = addresses.Select(IPAddress.Parse).ToArray();
        return this;
    }

    public Task<IPAddress[]> ResolveAsync(string host, CancellationToken ct)
        => _map.TryGetValue(host, out var a) ? Task.FromResult(a) : throw new SocketException((int)SocketError.HostNotFound);
}

/// <summary>أصل HTTP بدائي: يخدم طلبًا واحدًا في كل ServeOnceAsync ويغلق بعده.</summary>
internal sealed class HttpOrigin : IDisposable
{
    private readonly TcpListener _listener;

    public HttpOrigin()
    {
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
    }

    public int Port { get; }
    public string? ReceivedHead { get; private set; }

    public Task ServeOnceAsync(string body) => Task.Run(async () =>
    {
        using var socket = await _listener.AcceptSocketAsync();
        using var stream = new NetworkStream(socket, true);
        var request = await HttpRequestParser.ReadAsync(stream, CancellationToken.None);
        ReceivedHead = request is null ? null : $"{request.Method} {request.Target} {request.Version}\r\n" + string.Join("\r\n", request.Headers.Select(h => $"{h.Key}: {h.Value}"));
        var payload = Encoding.UTF8.GetBytes(body);
        await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: text/plain\r\nContent-Length: {payload.Length}\r\nConnection: close\r\n\r\n"));
        await stream.WriteAsync(payload);
        socket.Shutdown(SocketShutdown.Send);
        var buffer = new byte[1024];
        try { while (await stream.ReadAsync(buffer) > 0) { } } catch { }
    });

    public void Dispose() => _listener.Stop();
}

internal sealed record ResponseHead(int Status, Dictionary<string, string> Headers, byte[] Remainder);

internal static class Http
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    public static async Task<ResponseHead> ReadHeadAsync(Stream stream)
    {
        var buffer = new byte[64 * 1024];
        var filled = 0;
        while (true)
        {
            var idx = buffer.AsSpan(0, filled).IndexOf("\r\n\r\n"u8);
            if (idx >= 0)
            {
                var lines = Encoding.ASCII.GetString(buffer, 0, idx).Split("\r\n");
                var status = int.Parse(lines[0].Split(' ')[1]);
                var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var line in lines.Skip(1))
                {
                    var colon = line.IndexOf(':');
                    if (colon > 0) headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
                }
                return new ResponseHead(status, headers, buffer.AsSpan(idx + 4, filled - idx - 4).ToArray());
            }
            var n = await stream.ReadAsync(buffer.AsMemory(filled)).AsTask().WaitAsync(Timeout);
            if (n == 0) throw new EndOfStreamException($"closed after {filled} bytes");
            filled += n;
        }
    }

    public static async Task<byte[]> ReadBodyAsync(Stream stream, ResponseHead head)
    {
        var length = int.Parse(head.Headers["Content-Length"]);
        var body = new byte[length];
        var have = Math.Min(length, head.Remainder.Length);
        head.Remainder.AsSpan(0, have).CopyTo(body);
        while (have < length)
        {
            var n = await stream.ReadAsync(body.AsMemory(have)).AsTask().WaitAsync(Timeout);
            if (n == 0) throw new EndOfStreamException();
            have += n;
        }
        return body;
    }
}
