using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using RouteBridge.Core.Allowlist;
using RouteBridge.Core.Browser;
using RouteBridge.Core.Net;
using RouteBridge.Tunnel.Mux;

namespace RouteBridge.Proxy;

public sealed class ConnectProxyOptions
{
    public required IAllowlist Allowlist { get; init; }
    public IReadOnlyList<int> AllowedPorts { get; init; } = new[] { 80, 443 };
    /// <summary>IP المضيف العام كما يراه الخادم؛ يظهر في صفحة الفحص.</summary>
    public string PeerPublicIp { get; init; } = string.Empty;
    /// <summary>null = لا نفق (كل شيء مباشر؛ وضع self-hosted في أداة Spike).</summary>
    public IMuxConnection? Mux { get; init; }
    public IHostResolver Resolver { get; init; } = DnsHostResolver.Instance;
    public IOwnerPidChecker OwnerPidChecker { get; init; } = PermissiveOwnerPidChecker.Instance;
    /// <summary>null = لا فحص مالك (كل اتصال محلي مقبول).</summary>
    public IBrowserSession? Browser { get; init; }
    /// <summary>عند وجود Browser: هل يُرفض اتصال لا يمكن تحديد مالكه؟ الافتراضي true على Windows (fail-closed) وfalse على غيره.</summary>
    public bool? RejectUnknownOwner { get; init; }
    public TimeSpan DirectConnectTimeout { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan RequestHeadTimeout { get; init; } = TimeSpan.FromSeconds(30);
}

public sealed class ProxyCounters
{
    private long _accepted, _rejectedOwner, _tunneled, _direct, _directHttp, _rejected, _probeHits, _errors;
    public long Accepted => Volatile.Read(ref _accepted);
    public long RejectedByOwner => Volatile.Read(ref _rejectedOwner);
    public long Tunneled => Volatile.Read(ref _tunneled);
    public long DirectConnects => Volatile.Read(ref _direct);
    public long DirectHttpRequests => Volatile.Read(ref _directHttp);
    public long Rejected => Volatile.Read(ref _rejected);
    public long ProbeHits => Volatile.Read(ref _probeHits);
    public long Errors => Volatile.Read(ref _errors);
    internal void Accept() => Interlocked.Increment(ref _accepted);
    internal void RejectOwner() => Interlocked.Increment(ref _rejectedOwner);
    internal void Tunnel() => Interlocked.Increment(ref _tunneled);
    internal void Direct() => Interlocked.Increment(ref _direct);
    internal void DirectHttp() => Interlocked.Increment(ref _directHttp);
    internal void Reject() => Interlocked.Increment(ref _rejected);
    internal void Probe() => Interlocked.Increment(ref _probeHits);
    internal void Error() => Interlocked.Increment(ref _errors);
}

/// <summary>
/// Proxy محلي على 127.0.0.1:0 لمتصفح العمل (docs/protocol.md وخطة 7.5):
/// - فحص المالك لكل اتصال (PID ضمن Job Object المتصفح).
/// - CONNECT host:port (ws/wss كذلك، و[IPv6]:port): IP حرفي/محلي → 403؛ مسموح → OPEN عبر النفق و200 بعد OPEN_OK فقط
///   (OPEN_FAIL → 403/502/503 صادقة، وnot_allowed أثناء تباين إصدار القائمة → سقوط إلى المباشر)؛ غير مسموح → TCP مباشر (5 ث).
/// - http:// absolute-URI: check.routebridge → صفحة الفحص؛ مسموح → 307 إلى https://؛ غيره → طلب واحد لكل اتصال بصيغة origin-form
///   مع Connection: close وضخ حتى يغلق الأصل.
/// اتصال واحد = طلب واحد دائمًا؛ الردود المحلية تحمل Connection: close.
/// </summary>
public sealed class ConnectProxyServer : IAsyncDisposable
{
    private readonly ConnectProxyOptions _options;
    private readonly Socket _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentDictionary<Task, byte> _connections = new();
    private readonly bool _rejectUnknownOwner;
    private Task? _acceptLoop;
    private int _accepting;
    private int _stopped;
    private long _firstProbeHitTicks;

    public ConnectProxyServer(ConnectProxyOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        ArgumentNullException.ThrowIfNull(options.Allowlist);
        _rejectUnknownOwner = options.RejectUnknownOwner ?? OperatingSystem.IsWindows();
        _listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        _listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        _listener.Listen(64);
        LocalEndPoint = (IPEndPoint)_listener.LocalEndPoint!;
        Port = LocalEndPoint.Port;
    }

    public int Port { get; }
    public IPEndPoint LocalEndPoint { get; }
    public ProxyCounters Counters { get; } = new();
    public bool IsAccepting => Volatile.Read(ref _accepting) == 1;
    public int ActiveConnections => _connections.Count;

    /// <summary>يُرفع عند كل وصول لصفحة الفحص بوقت الوصول؛ FirstProbeHitAt يحفظ الأول.</summary>
    public event Action<DateTimeOffset>? ProbeHit;
    public DateTimeOffset? FirstProbeHitAt
    {
        get
        {
            var ticks = Volatile.Read(ref _firstProbeHitTicks);
            return ticks == 0 ? null : new DateTimeOffset(ticks, TimeSpan.Zero);
        }
    }

    public void Start()
    {
        if (Interlocked.CompareExchange(ref _accepting, 1, 0) != 0) throw new InvalidOperationException("proxy already started");
        _acceptLoop = Task.Run(() => AcceptLoopAsync(_cts.Token));
    }

    /// <summary>خطوة التنظيف 1 (docs/protocol.md القسم 7): لا اتصالات جديدة؛ الجارية تستمر حتى StopAsync.</summary>
    public void StopAccepting()
    {
        if (Interlocked.Exchange(ref _accepting, 0) == 0 && _acceptLoop is null) return;
        try { _listener.Close(); } catch { /* تجاهل */ }
    }

    public async Task StopAsync()
    {
        if (Interlocked.Exchange(ref _stopped, 1) != 0) return;
        StopAccepting();
        _cts.Cancel();
        if (_acceptLoop is not null) { try { await _acceptLoop.ConfigureAwait(false); } catch { /* تجاهل */ } }
        try { await Task.WhenAll(_connections.Keys).ConfigureAwait(false); } catch { /* المعالجات تبتلع أخطاءها */ }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _listener.Dispose();
        _cts.Dispose();
    }

    // ---------- accept ----------

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            Socket socket;
            try { socket = await _listener.AcceptAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException) when (ct.IsCancellationRequested || !IsAccepting) { break; }
            catch (SocketException) { continue; }

            Counters.Accept();
            var task = HandleConnectionAsync(socket, ct);
            _connections[task] = 0;
            _ = task.ContinueWith(t => _connections.TryRemove(t, out _), TaskScheduler.Default);
        }
    }

    private bool Admit(IPEndPoint? remote)
    {
        if (_options.Browser is null) return true;
        if (remote is null) return !_rejectUnknownOwner;
        int? pid;
        try { pid = _options.OwnerPidChecker.GetOwnerPid(remote, LocalEndPoint); }
        catch { pid = null; }
        if (pid is null) return !_rejectUnknownOwner;
        return _options.Browser.OwnsProcess(pid.Value);
    }

    private async Task HandleConnectionAsync(Socket socket, CancellationToken ct)
    {
        var remote = socket.RemoteEndPoint as IPEndPoint;
        if (!Admit(remote))
        {
            Counters.RejectOwner();
            try { socket.Close(0); } catch { /* تجاهل */ }
            return;
        }

        var client = new SocketStream(socket);
        await using (client)
        {
            try
            {
                ProxyRequest? request;
                using (var headCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    headCts.CancelAfter(_options.RequestHeadTimeout);
                    try { request = await HttpRequestParser.ReadAsync(client, headCts.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return; }
                }
                if (request is null) return;

                if (request.IsConnect) await HandleConnectAsync(client, request, ct).ConfigureAwait(false);
                else await HandleHttpAsync(client, request, ct).ConfigureAwait(false);
            }
            catch (HttpParseException)
            {
                await TryRespondAsync(client, 400, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
            catch (Exception)
            {
                Counters.Error();
            }
        }
    }

    // ---------- CONNECT ----------

    private async Task HandleConnectAsync(SocketStream client, ProxyRequest request, CancellationToken ct)
    {
        if (!HttpRequestParser.TryParseAuthority(request.Target, 443, out var host, out var port))
        {
            await TryRespondAsync(client, 400, ct).ConfigureAwait(false);
            return;
        }

        var route = ProxyRouter.Decide(host, port, _options.Allowlist, _options.AllowedPorts);
        if (route.Kind == RouteKind.Reject)
        {
            Counters.Reject();
            await TryRespondAsync(client, 403, ct).ConfigureAwait(false);
            return;
        }

        if (route.Kind == RouteKind.Tunnel && _options.Mux is { } mux)
        {
            MuxOpenResult open;
            try
            {
                open = await mux.OpenStreamAsync(route.Host, route.Port, ct).ConfigureAwait(false);
            }
            catch (MuxClosedException)
            {
                await TryRespondAsync(client, 502, ct).ConfigureAwait(false);
                return;
            }

            if (open.IsOpen)
            {
                await using var tunnel = open.Stream!;
                Counters.Tunnel();
                await WriteConnectEstablishedAsync(client, ct).ConfigureAwait(false);
                await PumpAsync(client, tunnel, request.Remainder, ct).ConfigureAwait(false);
                return;
            }

            if (open.Reason != OpenFailReason.NotAllowed)
            {
                var status = ProxyRouter.StatusForOpenFail(open.Reason ?? OpenFailReason.ConnectFailed);
                if (status == 403) Counters.Reject();
                await TryRespondAsync(client, status, ct).ConfigureAwait(false);
                return;
            }
            // not_allowed أثناء نافذة تباين إصدار القائمة: المضيف يعرف الأحدث؛ نسقط إلى المباشر (ADR-0004).
        }

        var origin = await DirectConnectAsync(route.Host, route.Port, ct).ConfigureAwait(false);
        if (origin is null)
        {
            await TryRespondAsync(client, 502, ct).ConfigureAwait(false);
            return;
        }
        await using (origin)
        {
            Counters.Direct();
            await WriteConnectEstablishedAsync(client, ct).ConfigureAwait(false);
            await PumpAsync(client, origin, request.Remainder, ct).ConfigureAwait(false);
        }
    }

    // ---------- plain http:// ----------

    private async Task HandleHttpAsync(SocketStream client, ProxyRequest request, CancellationToken ct)
    {
        string host;
        int port;
        string path;
        if (HttpRequestParser.TryParseHttpUri(request.Target, out host, out port, out path))
        {
            // absolute-URI كما يرسله المتصفح إلى Proxy
        }
        else if (request.Target.StartsWith('/') && request.Header("Host") is { Length: > 0 } hostHeader
                 && HttpRequestParser.TryParseAuthority(hostHeader, 80, out host, out port))
        {
            path = request.Target; // origin-form (curl مباشرة على المنفذ، أو صفحة الفحص بترويسة Host)
        }
        else
        {
            await TryRespondAsync(client, 400, ct).ConfigureAwait(false);
            return;
        }

        if (string.Equals(host, ProbePage.Host, StringComparison.OrdinalIgnoreCase))
        {
            await ServeProbeAsync(client, ct).ConfigureAwait(false);
            return;
        }

        var route = ProxyRouter.Decide(host, port, _options.Allowlist, _options.AllowedPorts);
        if (route.Kind == RouteKind.Reject)
        {
            Counters.Reject();
            await TryRespondAsync(client, 403, ct).ConfigureAwait(false);
            return;
        }

        if (ProxyRouter.HostIsAllowlisted(route.Host, _options.Allowlist))
        {
            // لا بايتات نصية عبر النفق: تحويل محلي إلى https
            var location = port == 80 ? $"https://{route.Host}{path}" : $"https://{route.Host}:{port}{path}";
            await TryRespondAsync(client, 307, ct, ("Location", location)).ConfigureAwait(false);
            return;
        }

        // TODO(week 3+): احترام Proxy النظام على المسار المباشر (إرسال الطلب/CONNECT إلى Proxy النظام إن وُجد؛ خطة 8.5).
        var origin = await DirectConnectAsync(route.Host, port, ct).ConfigureAwait(false);
        if (origin is null)
        {
            await TryRespondAsync(client, 502, ct).ConfigureAwait(false);
            return;
        }
        await using (origin)
        {
            Counters.DirectHttp();
            var head = BuildOriginFormHead(request, path);
            await origin.WriteAsync(head, ct).ConfigureAwait(false);
            // طلب واحد لكل اتصال: نضخ الجسم إن وُجد ونعيد الرد حتى يغلق الأصل، ثم نغلق نحو المتصفح.
            await PumpUntilOriginClosesAsync(client, origin, request.Remainder, ct).ConfigureAwait(false);
        }
    }

    /// <summary>absolute-URI → origin-form؛ حذف Proxy-Connection/Proxy-Authorization/Keep-Alive؛ Connection: close.</summary>
    public static byte[] BuildOriginFormHead(ProxyRequest request, string path)
    {
        var sb = new StringBuilder();
        sb.Append(request.Method).Append(' ').Append(path).Append(' ').Append(request.Version).Append("\r\n");
        foreach (var (name, value) in request.Headers)
        {
            if (name.Equals("Proxy-Connection", StringComparison.OrdinalIgnoreCase)) continue;
            if (name.Equals("Proxy-Authorization", StringComparison.OrdinalIgnoreCase)) continue;
            if (name.Equals("Connection", StringComparison.OrdinalIgnoreCase)) continue;
            if (name.Equals("Keep-Alive", StringComparison.OrdinalIgnoreCase)) continue;
            sb.Append(name).Append(": ").Append(value).Append("\r\n");
        }
        sb.Append("Connection: close\r\n\r\n");
        return Encoding.Latin1.GetBytes(sb.ToString());
    }

    private async Task ServeProbeAsync(SocketStream client, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        Interlocked.CompareExchange(ref _firstProbeHitTicks, now.UtcTicks, 0);
        Counters.Probe();
        try { ProbeHit?.Invoke(now); } catch { /* المستمع مسؤول عن أخطائه */ }
        var body = ProbePage.HtmlBytes(_options.PeerPublicIp);
        await TryRespondAsync(client, 200, ct, body, "text/html; charset=utf-8", ("Cache-Control", "no-store")).ConfigureAwait(false);
    }

    // ---------- direct ----------

    /// <summary>حل الاسم ثم Socket.ConnectAsync(IPAddress[], port) بمهلة 5 ثوانٍ للعملية كلها. null عند الفشل.</summary>
    private async Task<SocketStream?> DirectConnectAsync(string host, int port, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(_options.DirectConnectTimeout);
        Socket? socket = null;
        try
        {
            var addresses = await _options.Resolver.ResolveAsync(host, cts.Token).ConfigureAwait(false);
            addresses = addresses.Where(a => a.AddressFamily is AddressFamily.InterNetwork or AddressFamily.InterNetworkV6).ToArray();
            if (addresses.Length == 0) return null;
            socket = CreateSocket(addresses);
            await socket.ConnectAsync(addresses, port, cts.Token).ConfigureAwait(false);
            return new SocketStream(socket);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            socket?.Dispose();
            throw;
        }
        catch (Exception)
        {
            socket?.Dispose();
            return null;
        }
    }

    private static Socket CreateSocket(IPAddress[] addresses)
    {
        var needV6 = addresses.Any(a => a.AddressFamily == AddressFamily.InterNetworkV6);
        var needV4 = addresses.Any(a => a.AddressFamily == AddressFamily.InterNetwork);
        if (needV6)
        {
            try { return new Socket(AddressFamily.InterNetworkV6, SocketType.Stream, ProtocolType.Tcp) { DualMode = needV4 }; }
            catch (SocketException) when (needV4) { }
        }
        return new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
    }

    // ---------- helpers ----------

    private static async Task PumpAsync(Stream client, Stream far, byte[] remainder, CancellationToken ct)
    {
        if (remainder.Length > 0)
        {
            await far.WriteAsync(remainder, ct).ConfigureAwait(false);
            await far.FlushAsync(ct).ConfigureAwait(false);
        }
        await StreamPump.RunAsync(client, far, ct).ConfigureAwait(false);
    }

    /// <summary>الرد يُنسخ حتى يغلق الأصل؛ عندها ينتهي الطلب ولا ننتظر المتصفح (يُغلق نحوه بعد العودة).</summary>
    private static async Task PumpUntilOriginClosesAsync(Stream client, Stream origin, byte[] remainder, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (remainder.Length > 0)
        {
            await origin.WriteAsync(remainder, ct).ConfigureAwait(false);
            await origin.FlushAsync(ct).ConfigureAwait(false);
        }
        var upload = Task.Run(async () =>
        {
            try
            {
                await StreamPump.CopyAsync(client, origin, cts.Token).ConfigureAwait(false);
                if (origin is IHalfClosable h) await h.CompleteWritingAsync(cts.Token).ConfigureAwait(false);
            }
            catch { /* المتصفح أغلق أو أُلغي */ }
        }, CancellationToken.None);
        try { await StreamPump.CopyAsync(origin, client, cts.Token).ConfigureAwait(false); }
        catch { /* الأصل قطع الاتصال أو المتصفح أغلق */ }
        cts.Cancel();
        await upload.ConfigureAwait(false);
        if (client is IHalfClosable half) await half.CompleteWritingAsync(CancellationToken.None).ConfigureAwait(false);
    }

    private static async Task WriteConnectEstablishedAsync(Stream client, CancellationToken ct)
    {
        await client.WriteAsync("HTTP/1.1 200 Connection Established\r\n\r\n"u8.ToArray(), ct).ConfigureAwait(false);
        await client.FlushAsync(ct).ConfigureAwait(false);
    }

    private static Task TryRespondAsync(Stream client, int status, CancellationToken ct, params (string Name, string Value)[] headers)
        => TryRespondAsync(client, status, ct, null, null, headers);

    private static async Task TryRespondAsync(Stream client, int status, CancellationToken ct, byte[]? body, string? contentType, params (string Name, string Value)[] headers)
    {
        try
        {
            var sb = new StringBuilder();
            sb.Append("HTTP/1.1 ").Append(status).Append(' ').Append(ProxyRouter.ReasonPhrase(status)).Append("\r\n");
            foreach (var (name, value) in headers) sb.Append(name).Append(": ").Append(value).Append("\r\n");
            if (contentType is not null) sb.Append("Content-Type: ").Append(contentType).Append("\r\n");
            sb.Append("Content-Length: ").Append(body?.Length ?? 0).Append("\r\n");
            sb.Append("Proxy-Connection: close\r\nConnection: close\r\n\r\n");
            await client.WriteAsync(Encoding.ASCII.GetBytes(sb.ToString()), ct).ConfigureAwait(false);
            if (body is { Length: > 0 }) await client.WriteAsync(body, ct).ConfigureAwait(false);
            await client.FlushAsync(ct).ConfigureAwait(false);
            if (client is IHalfClosable h) await h.CompleteWritingAsync(ct).ConfigureAwait(false);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            // المتصفح أغلق؛ لا شيء يُفعل
        }
    }
}
