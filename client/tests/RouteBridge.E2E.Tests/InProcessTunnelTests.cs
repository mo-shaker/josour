using System.Net;
using System.Net.Sockets;
using System.Text;
using RouteBridge.Core.Net;
using RouteBridge.Core.Tunnel;
using RouteBridge.Proxy;

namespace RouteBridge.E2E.Tests;

/// <summary>
/// المسار كاملًا داخل العملية عبر جلستَي <see cref="RouteBridge.Tunnel.TunnelSession"/> (بلا ربط يدوي):
/// المضيف يشغّل سياسة الخروج، والضيف يشغّل الـ Proxy، ومتصفح وهمي (مقبس خام أو HttpClient بـ Proxy) يمر عليه.
/// </summary>
public class InProcessTunnelTests : IAsyncLifetime
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);
    private InProcessTunnelPair _pair = null!;

    public async Task InitializeAsync() => _pair = await InProcessTunnelPair.CreateAsync();

    public async Task DisposeAsync() => await _pair.DisposeAsync();

    [Fact]
    public async Task Connect_Allowlisted_FlowsThroughTunnel_CountsBytes_CollectsDomain()
    {
        Assert.Contains(_pair.HostResult.TlsVersion, new[] { "1.2", "1.3" });
        Assert.Equal(CandidateType.Lan, _pair.HostResult.WinnerType);
        Assert.Equal(TunnelState.Connected, _pair.Host.State);
        Assert.Equal(TunnelState.Connected, _pair.Guest.State);

        var serve = _pair.Origin.ServeOnceAsync("tunnelled body");
        using var client = await _pair.ConnectToProxyAsync();
        var stream = client.GetStream();

        await stream.WriteAsync(E2EWait.Ascii($"CONNECT site.test:{_pair.OriginPort} HTTP/1.1\r\nHost: site.test:{_pair.OriginPort}\r\n\r\n"));
        var connectHead = await Http.ReadHeadAsync(stream);
        Assert.Equal(200, connectHead.Status);
        Assert.Empty(connectHead.Remainder);

        var request = E2EWait.Ascii("GET /via-tunnel HTTP/1.1\r\nHost: site.test\r\n\r\n");
        await stream.WriteAsync(request);
        var head = await Http.ReadHeadAsync(stream);
        Assert.Equal(200, head.Status);
        var body = await Http.ReadBodyAsync(stream, head);
        Assert.Equal("tunnelled body", Encoding.UTF8.GetString(body));

        // EOF من الأصل ينتقل عبر النفق إلى المتصفح (إغلاق نصفي)
        var one = new byte[1];
        Assert.Equal(0, await stream.ReadAsync(one).AsTask().WaitAsync(Timeout));
        // المتصفح يغلق جانبه → ينتقل عبر النفق كـ Shutdown(Send) نحو الأصل
        client.Client.Shutdown(SocketShutdown.Send);
        await serve.WaitAsync(Timeout);
        Assert.StartsWith("GET /via-tunnel HTTP/1.1", _pair.Origin.ReceivedHead);

        // الإحصاءات والنطاقات تُقرأ من الجلسة نفسها (وهي ما يرسله التطبيق في session.stats/session.end)
        Assert.Equal(request.Length, _pair.Host.Stats.BytesUp);
        Assert.True(_pair.Host.Stats.BytesDown >= body.Length, $"down={_pair.Host.Stats.BytesDown}");
        Assert.Contains("site.test", _pair.Host.DomainsSeen);
        Assert.Empty(_pair.Guest.DomainsSeen);
        Assert.Equal(1, _pair.HostHandler.OpensOk);
        Assert.Equal(1, _pair.ProxyServer.Counters.Tunneled);
        Assert.True(_pair.Guest.Stats.BytesUp > request.Length);

        Assert.True(await E2EWait.UntilAsync(() => _pair.HostHandler.Limiter.Active == 0, TimeSpan.FromSeconds(5)));
        Assert.True(await E2EWait.UntilAsync(() => _pair.Host.Stats.OpenStreams == 0, TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task Http_NotAllowlisted_GoesDirect_NeverTouchingTheTunnel()
    {
        var serve = _pair.Origin.ServeOnceAsync("direct body");
        using var http = _pair.NewProxiedClient();
        var response = await http.GetAsync($"http://direct.test:{_pair.OriginPort}/direct").WaitAsync(Timeout);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("direct body", await response.Content.ReadAsStringAsync());
        await serve.WaitAsync(Timeout);
        Assert.StartsWith("GET /direct HTTP/1.1", _pair.Origin.ReceivedHead);
        Assert.Contains("Connection: close", _pair.Origin.ReceivedHead);

        Assert.DoesNotContain("direct.test", _pair.Host.DomainsSeen);
        Assert.Equal(0, _pair.HostHandler.OpensOk);
        Assert.Equal(0, _pair.HostHandler.OpensFailed);
        Assert.Equal(1, _pair.ProxyServer.Counters.DirectHttpRequests);
    }

    [Fact]
    public async Task ProbePage_IsServed_AndRaisesProbeSeenOnTheSession()
    {
        var seen = 0;
        _pair.Guest.ProbeSeen += () => Interlocked.Increment(ref seen);
        Assert.Equal(ProbePage.Url, _pair.Guest.Proxy!.ProbeUrl);

        using var http = _pair.NewProxiedClient();
        var html = await http.GetStringAsync(_pair.Guest.Proxy.ProbeUrl).WaitAsync(Timeout);

        Assert.Contains($"Tunnel active. Sites will see: {InProcessTunnelPair.PeerPublicIp}", html);
        Assert.True(await E2EWait.UntilAsync(() => seen == 1, TimeSpan.FromSeconds(5)));
        Assert.NotNull(_pair.Guest.ProbeSeenAt);
        Assert.NotNull(_pair.ProxyServer.FirstProbeHitAt);
        Assert.Equal(0, _pair.HostHandler.OpensOk); // صفحة الفحص لا تعبر النفق
    }

    [Fact]
    public async Task Connect_PrivateIpLiteral_RejectedLocally_NeverReachesHost()
    {
        using var client = await _pair.ConnectToProxyAsync();
        var stream = client.GetStream();
        await stream.WriteAsync(E2EWait.Ascii("CONNECT 192.168.1.1:443 HTTP/1.1\r\n\r\n"));
        Assert.Equal(403, (await Http.ReadHeadAsync(stream)).Status);

        using var loopbackClient = await _pair.ConnectToProxyAsync();
        var loopbackStream = loopbackClient.GetStream();
        await loopbackStream.WriteAsync(E2EWait.Ascii($"CONNECT 127.0.0.1:{_pair.OriginPort} HTTP/1.1\r\n\r\n"));
        Assert.Equal(403, (await Http.ReadHeadAsync(loopbackStream)).Status);

        using var localName = await _pair.ConnectToProxyAsync();
        var localNameStream = localName.GetStream();
        await localNameStream.WriteAsync(E2EWait.Ascii("CONNECT localhost:443 HTTP/1.1\r\n\r\n"));
        Assert.Equal(403, (await Http.ReadHeadAsync(localNameStream)).Status);

        Assert.Equal(0, _pair.HostHandler.OpensOk + _pair.HostHandler.OpensFailed);
        Assert.Equal(3, _pair.ProxyServer.Counters.Rejected);
    }

    [Fact]
    public async Task Connect_AllowlistedHost_WrongPort_IsNotTunnelled()
    {
        using var client = await _pair.ConnectToProxyAsync();
        var stream = client.GetStream();
        // المنفذ 443 ليس ضمن قيد المدخل site.test:<port> → غير مسموح → محاولة مباشرة → لا خادم هناك → 502
        await stream.WriteAsync(E2EWait.Ascii("CONNECT site.test:443 HTTP/1.1\r\n\r\n"));
        Assert.Equal(502, (await Http.ReadHeadAsync(stream)).Status);
        Assert.Equal(0, _pair.HostHandler.OpensOk + _pair.HostHandler.OpensFailed);
    }

    [Fact]
    public async Task AllowlistSkew_HostSaysNotAllowed_GuestFallsBackToDirect()
    {
        // المضيف بقائمة أحدث لا تحوي site.test: OPEN_FAIL(not_allowed) → الضيف يسقط إلى المباشر (ADR-0004)
        await using var skewed = await InProcessTunnelPair.CreateAsync(hostAllowlistOverride: new[] { "other.example" });
        var serve = skewed.Origin.ServeOnceAsync("fallback body");
        using var client = await skewed.ConnectToProxyAsync();
        var stream = client.GetStream();

        await stream.WriteAsync(E2EWait.Ascii($"CONNECT site.test:{skewed.OriginPort} HTTP/1.1\r\n\r\n"));
        Assert.Equal(200, (await Http.ReadHeadAsync(stream)).Status);
        await stream.WriteAsync(E2EWait.Ascii("GET /fb HTTP/1.1\r\nHost: site.test\r\n\r\n"));
        var head = await Http.ReadHeadAsync(stream);
        Assert.Equal("fallback body", Encoding.UTF8.GetString(await Http.ReadBodyAsync(stream, head)));
        client.Client.Shutdown(SocketShutdown.Send);
        await serve.WaitAsync(Timeout);

        Assert.Equal(1, skewed.HostHandler.OpensFailed);
        Assert.Equal(0, skewed.HostHandler.OpensOk);
        Assert.Equal(1, skewed.ProxyServer.Counters.DirectConnects);
        Assert.Empty(skewed.Host.DomainsSeen);
    }
}

/// <summary>دورة حياة الجلسة من طرف إلى طرف: الإنهاء، الموت المفاجئ، وفشل الاتصال.</summary>
public class InProcessTunnelLifecycleTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    [Fact]
    public async Task EndAsync_LeavesNothingRunning_AndIsIdempotent()
    {
        var pair = await InProcessTunnelPair.CreateAsync();
        var proxyPort = pair.ProxyPort;
        var guestListenPort = pair.Guest.ListenPort;
        var hostListenPort = pair.Host.ListenPort;
        var guestStates = new List<TunnelState>();
        pair.Guest.StateChanged += guestStates.Add;

        // حركة حقيقية أولًا حتى تكون الإحصاءات النهائية ذات معنى
        var serve = pair.Origin.ServeOnceAsync("bye");
        using (var client = await pair.ConnectToProxyAsync())
        {
            var stream = client.GetStream();
            await stream.WriteAsync(E2EWait.Ascii($"CONNECT site.test:{pair.OriginPort} HTTP/1.1\r\n\r\n"));
            Assert.Equal(200, (await Http.ReadHeadAsync(stream)).Status);
            await stream.WriteAsync(E2EWait.Ascii("GET /bye HTTP/1.1\r\nHost: site.test\r\n\r\n"));
            var head = await Http.ReadHeadAsync(stream);
            await Http.ReadBodyAsync(stream, head);
            client.Client.Shutdown(SocketShutdown.Send);
        }
        await serve.WaitAsync(Timeout);

        await pair.Guest.EndAsync(TunnelEndReason.GuestEnded, CancellationToken.None);
        await pair.Host.EndAsync(TunnelEndReason.HostEnded, CancellationToken.None);

        Assert.Equal(TunnelState.Ended, pair.Guest.State);
        Assert.Equal(TunnelState.Ended, pair.Host.State);
        Assert.True(pair.Guest.MuxClosed);
        Assert.True(pair.Host.MuxClosed);
        Assert.True(pair.Guest.CertificateDisposed);
        Assert.True(pair.Host.CertificateDisposed);
        Assert.False(pair.ProxyServer.IsAccepting);
        Assert.True(await E2EWait.PortRefusesAsync(proxyPort), "proxy port still accepts");
        Assert.True(await E2EWait.PortRefusesAsync(guestListenPort), "guest tunnel listener still accepts");
        Assert.True(await E2EWait.PortRefusesAsync(hostListenPort), "host tunnel listener still accepts");

        // الإحصاءات والنطاقات محفوظة بعد الإنهاء لرسالة session.end
        Assert.True(pair.Host.Stats.BytesUp > 0);
        Assert.True(pair.Host.Stats.BytesDown > 0);
        Assert.Equal(0, pair.Host.Stats.OpenStreams);
        Assert.Contains("site.test", pair.Host.DomainsSeen);

        // التكرار بلا أثر: نفس المهمة، ولا حالة جديدة
        var again = pair.Guest.EndAsync(TunnelEndReason.ProtocolError, CancellationToken.None);
        Assert.Same(again, pair.Guest.EndAsync(TunnelEndReason.Expired, CancellationToken.None));
        await again;
        await pair.DisposeAsync();
        Assert.Equal(new[] { TunnelState.Ended }, guestStates);
        Assert.Equal("GuestEnded", pair.Guest.Diagnostics["end_reason"]);
    }

    [Fact]
    public async Task KillingTheHostTransport_RaisesDied_WithDisconnectReason_AndIsNotACleanEnd()
    {
        await using var pair = await InProcessTunnelPair.CreateAsync();
        TunnelEndReason? hostReason = null;
        var hostStates = new List<TunnelState>();
        pair.Host.Died += r => hostReason = r;
        pair.Host.StateChanged += hostStates.Add;

        pair.HostTransport.KillAll();

        // المضيف يكتشف فورًا لأن التخلّص محلي. أما كشف المستخدم فيعتمد على EOF أو مهلة الحيوية،
        // وتسميته للطرف المختفي يملكها اختبار حتمي في طبقة النفق:
        // TunnelSessionTests.Pair_KillingTransport_RaisesDied_WithDisconnectReason_OnBothSides.
        // تكراره هنا فوق مكدس أثقل يضاعف التعرّض للتقطع بلا تغطية إضافية، فنكتفي هنا بما يخص التكامل:
        // النفق ميت، والـ Proxy يتوقف عن التظاهر بالنجاح، والموت ليس إنهاءً نظيفًا.
        Assert.True(await E2EWait.UntilAsync(() => hostReason is not null, Timeout), $"host={hostReason}");
        Assert.Equal(TunnelEndReason.GuestDisconnected, hostReason);
        Assert.Equal("faulted", pair.Host.Diagnostics["mux_completion"]);

        // موت ≠ إنهاء نظيف: لا انتقال إلى Ended ولا سبب إنهاء حتى يقرر التطبيق
        Assert.Empty(hostStates);
        Assert.Equal(TunnelState.Connected, pair.Host.State);
        Assert.False(pair.Host.Diagnostics.ContainsKey("end_reason"));

        // بعد الموت: الـ Proxy يرد 502 لأن النفق مغلق (لا يتظاهر بالنجاح)
        using var client = await pair.ConnectToProxyAsync();
        var stream = client.GetStream();
        await stream.WriteAsync(E2EWait.Ascii($"CONNECT site.test:{pair.OriginPort} HTTP/1.1\r\n\r\n"));
        Assert.Equal(502, (await Http.ReadHeadAsync(stream)).Status);

        // والتطبيق ينهي بالسبب المقترح؛ الإنهاء بعد الموت لا يرمي
        await pair.Host.EndAsync(hostReason!.Value, CancellationToken.None);
        Assert.Equal(TunnelState.Ended, pair.Host.State);
        Assert.Equal("GuestDisconnected", pair.Host.Diagnostics["end_reason"]);
    }

    [Fact]
    public async Task ConnectAsync_Timeout_ReturnsNotConnected_WithPerCandidateDiagnostics()
    {
        var deadPort = ClosedPort();
        await using var session = new RouteBridge.Tunnel.TunnelSession(
            new SessionMaterial(Guid.NewGuid(), TunnelRole.Guest, new byte[32], DateTimeOffset.UtcNow.AddMinutes(30), true, InProcessTunnelPair.PeerPublicIp),
            new RouteBridge.Tunnel.TunnelSessionOptions
            {
                BindAddress = IPAddress.Loopback,
                CandidateSource = () => new LoopbackCandidateSource(),
                GuestProxy = ProxyTunnelAdapter.Create,
            });

        var local = await session.PrepareAsync(CancellationToken.None);
        Assert.Equal(64, local.CertFingerprintSha256Hex.Length);

        var peer = new PeerEndpointInfo(
            Convert.ToHexString(new byte[32]).ToLowerInvariant(),
            new[]
            {
                new CandidateEndpoint(CandidateType.Public, "127.0.0.1", deadPort),
                new CandidateEndpoint(CandidateType.Upnp, "127.0.0.1", deadPort),
            });
        var result = await session.ConnectAsync(peer, TimeSpan.FromSeconds(2), CancellationToken.None);

        Assert.False(result.Connected);
        Assert.Null(result.WinnerType);
        Assert.Null(result.TlsVersion);
        Assert.Equal("timeout", result.FailureReason);
        Assert.Null(session.Proxy);

        var connect = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(session.Diagnostics["connect"]);
        var rows = Assert.IsType<List<Dictionary<string, object?>>>(connect["candidates"]);
        Assert.Equal(2, rows.Count);
        Assert.All(rows, row =>
        {
            Assert.Equal(deadPort, row["port"]);
            Assert.Equal("connect", row["stage"]);
            Assert.NotNull(row["error"]);
        });
        Assert.Equal(new[] { "public", "upnp" }, rows.Select(r => (string?)r["type"]));

        await session.EndAsync(TunnelEndReason.ConnectFailed, CancellationToken.None);
        Assert.Equal(TunnelState.Ended, session.State);
    }

    private static int ClosedPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
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
