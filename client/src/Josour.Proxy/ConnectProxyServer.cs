using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Josour.Core.Allowlist;
using Josour.Core.Browser;
using Josour.Core.Net;
using Josour.Tunnel.Mux;

namespace Josour.Proxy;

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
    /// <summary>
    /// فحص الحظر لعنوان واحد على المسار المباشر بعد حل الاسم. الافتراضي <see cref="IpRangePolicy.IsBlocked(IPAddress, IEnumerable{IPAddress}?)"/>:
    /// اسم يحل إلى loopback أو عنوان خاص لا يُوصَل إليه، فلا يصير الـ Proxy المحلي جسرًا نحو ما يسمعه هذا الجهاز
    /// (مستمع النفق نفسه، منفذ الـ Relay، خدمات 127.0.0.1). يُستبدل في الاختبارات داخل العملية فقط للسماح بـ loopback.
    /// </summary>
    public Func<IPAddress, bool>? AddressBlocker { get; init; }
    /// <summary>
    /// عند وجود Browser: هل يُرفض اتصال لا يمكن تحديد مالكه؟ **الافتراضي `true` على كل نظام** (fail-closed).
    /// <para>
    /// كان الافتراض `OperatingSystem.IsWindows()`، أي «ارفض على Windows واقبل على غيره». ومع
    /// <see cref="PermissiveOwnerPidChecker"/> — وهي كل ما يوجد خارج Windows — كان معنى ذلك أن الـ Proxy المحلي
    /// **يقبل كل عملية على الجهاز**، لا متصفح العمل وحده. لم يكن ذلك قرارًا اتُّخذ، بل أثرًا جانبيًا لافتراض،
    /// وكان يُسقط معيار القبول رقم 10 بصمت على أي نظام غير Windows.
    /// </para>
    /// <para>
    /// من أراد «اقبل كل شيء» فليقل ذلك صراحةً بـ `false` — كما تفعل الاختبارات داخل العملية وأداة Spike. الفرق
    /// بين اختيارٍ مُعلَن وافتراضٍ صامت هو كل الفرق هنا.
    /// </para>
    /// </summary>
    public bool? RejectUnknownOwner { get; init; }
    public TimeSpan DirectConnectTimeout { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan RequestHeadTimeout { get; init; } = TimeSpan.FromSeconds(30);
    /// <summary>
    /// Proxy النظام للمسار **المباشر** فقط (الخطة 8.5). الافتراضي إعدادات الجهاز:
    /// WinHTTP/WinINET على Windows ومتغيرات البيئة على غيرها، بقائمة تجاوزها.
    /// المسار المسموح به لا يمر من هنا أبدًا — يذهب عبر الـ mux إلى المضيف.
    /// تُحقن <see cref="NoSystemProxy.Instance"/> في الاختبارات كي لا تعتمد على إعدادات الجهاز.
    /// </summary>
    public ISystemProxyResolver SystemProxy { get; init; } = SystemProxyResolver.Default;
}

public sealed class ProxyCounters
{
    private long _accepted, _rejectedOwner, _tunneled, _direct, _directHttp, _rejected, _probeHits, _errors, _viaSystemProxy;
    public long Accepted => Volatile.Read(ref _accepted);
    public long RejectedByOwner => Volatile.Read(ref _rejectedOwner);
    public long Tunneled => Volatile.Read(ref _tunneled);
    public long DirectConnects => Volatile.Read(ref _direct);
    public long DirectHttpRequests => Volatile.Read(ref _directHttp);
    public long Rejected => Volatile.Read(ref _rejected);
    public long ProbeHits => Volatile.Read(ref _probeHits);
    public long Errors => Volatile.Read(ref _errors);
    /// <summary>كم طلبًا مباشرًا مرّ عبر Proxy النظام بدل مقبس خام (تشخيص شبكات الشركات).</summary>
    public long ViaSystemProxy => Volatile.Read(ref _viaSystemProxy);
    internal void SystemProxyUsed() => Interlocked.Increment(ref _viaSystemProxy);
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
/// - http:// absolute-URI: check.josour → صفحة الفحص؛ مسموح → 307 إلى https://؛ غيره → طلب واحد لكل اتصال بصيغة origin-form
///   مع Connection: close وضخ حتى يغلق الأصل.
/// اتصال واحد = طلب واحد دائمًا؛ الردود المحلية تحمل Connection: close.
/// </summary>
public sealed class ConnectProxyServer : IAsyncDisposable
{
    /// <summary>لماذا فشل الاتصال المباشر: سياسة العناوين (403) أم تعذّر الوصول (502).</summary>
    private enum DirectFailure { None, Blocked, Unreachable }

    private readonly ConnectProxyOptions _options;
    private readonly Socket _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentDictionary<Task, byte> _connections = new();
    private readonly bool _rejectUnknownOwner;
    private readonly Func<IPAddress, bool> _isBlocked;
    private Task? _acceptLoop;
    private int _accepting;
    private int _stopped;
    private long _firstProbeHitTicks;

    public ConnectProxyServer(ConnectProxyOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        ArgumentNullException.ThrowIfNull(options.Allowlist);
        _rejectUnknownOwner = options.RejectUnknownOwner ?? true;
        // A browser to check against, a checker that never identifies anyone, and a policy of refusing
        // whoever it cannot identify: that combination admits nothing, ever. It is not a strict policy,
        // it is a wiring mistake, and it shipped once - every connection the work browser made was
        // refused and the only symptom was a session that would not browse. Refuse to start instead.
        if (options.Browser is not null && _rejectUnknownOwner && options.OwnerPidChecker is PermissiveOwnerPidChecker)
        {
            throw new ArgumentException(
                "the proxy would refuse every connection: a browser is configured for owner checks, but the owner " +
                "checker cannot identify anyone and unknown owners are rejected. Supply a real IOwnerPidChecker, " +
                "or set RejectUnknownOwner = false if this really is meant to admit everything.",
                nameof(options));
        }
        _isBlocked = options.AddressBlocker ?? (address => IpRangePolicy.IsBlocked(address));
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

        // شبكة شركة بـ Proxy إجباري: المسار المباشر يمرر CONNECT إلى Proxy النظام (الخطة 8.5).
        if (ResolveSystemProxy(route.Host, route.Port, secure: true) is { } upstreamProxy)
        {
            await ConnectViaSystemProxyAsync(client, request, upstreamProxy, route.Host, route.Port, ct).ConfigureAwait(false);
            return;
        }

        var (origin, failure) = await DirectConnectAsync(route.Host, route.Port, ct).ConfigureAwait(false);
        if (origin is null)
        {
            if (failure == DirectFailure.Blocked) Counters.Reject();
            await TryRespondAsync(client, failure == DirectFailure.Blocked ? 403 : 502, ct).ConfigureAwait(false);
            return;
        }
        await using (origin)
        {
            Counters.Direct();
            await WriteConnectEstablishedAsync(client, ct).ConfigureAwait(false);
            await PumpAsync(client, origin, request.Remainder, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// CONNECT عبر Proxy النظام. 2xx → 200 للمتصفح وضخ؛ 407 → يُنقل للمتصفح مع <c>Proxy-Authenticate</c>
    /// ليعيد المحاولة بـ <c>Proxy-Authorization</c> (Basic/Digest بجولة واحدة)؛ غير ذلك → 502 صادق.
    /// </summary>
    private async Task ConnectViaSystemProxyAsync(SocketStream client, ProxyRequest request, SystemProxyEndpoint proxy, string host, int port, CancellationToken ct)
    {
        var upstream = await UpstreamProxyClient.DialAsync(proxy, _options.Resolver, _options.DirectConnectTimeout, ct).ConfigureAwait(false);
        if (upstream is null)
        {
            await TryRespondAsync(client, 502, ct).ConfigureAwait(false);
            return;
        }

        var result = await UpstreamProxyClient.ConnectAsync(
            upstream, host, port, request.Header("Proxy-Authorization"), _options.RequestHeadTimeout, ct).ConfigureAwait(false);

        if (!result.IsEstablished)
        {
            if (result.Status == 407)
            {
                var headers = result.ProxyAuthenticate.Select(v => ("Proxy-Authenticate", v)).ToArray();
                await TryRespondAsync(client, 407, ct, headers).ConfigureAwait(false);
                return;
            }
            await TryRespondAsync(client, 502, ct).ConfigureAwait(false);
            return;
        }

        await using var tunnel = result.Stream!;
        Counters.Direct();
        Counters.SystemProxyUsed();
        await WriteConnectEstablishedAsync(client, ct).ConfigureAwait(false);
        // بايتات وصلت بعد رأس رد الـ Proxy تخص النفق نفسه ويجب أن تصل المتصفح.
        if (result.Remainder.Length > 0)
        {
            await client.WriteAsync(result.Remainder, ct).ConfigureAwait(false);
            await client.FlushAsync(ct).ConfigureAwait(false);
        }
        await PumpAsync(client, tunnel, request.Remainder, ct).ConfigureAwait(false);
    }

    /// <summary>Proxy النظام لهذه الوجهة، أو null إن لم يُضبط أو كانت ضمن قائمة التجاوز. لا يرمي.</summary>
    private SystemProxyEndpoint? ResolveSystemProxy(string host, int port, bool secure)
    {
        var resolver = _options.SystemProxy;
        if (resolver is null) return null;
        try
        {
            var builder = new UriBuilder(secure ? Uri.UriSchemeHttps : Uri.UriSchemeHttp, host, port);
            return resolver.Resolve(builder.Uri);
        }
        catch (Exception)
        {
            return null; // اسم لا يصلح لبناء Uri: نكمل مباشرةً
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

        // شبكة شركة بـ Proxy إجباري: الطلب يذهب إلى Proxy النظام بصيغة absolute-URI (الخطة 8.5).
        if (ResolveSystemProxy(route.Host, port, secure: false) is { } upstreamProxy)
        {
            var upstream = await UpstreamProxyClient.DialAsync(upstreamProxy, _options.Resolver, _options.DirectConnectTimeout, ct).ConfigureAwait(false);
            if (upstream is null)
            {
                await TryRespondAsync(client, 502, ct).ConfigureAwait(false);
                return;
            }
            await using (upstream)
            {
                Counters.DirectHttp();
                Counters.SystemProxyUsed();
                var proxyHead = BuildProxyFormHead(request, route.Host, port, path);
                await upstream.WriteAsync(proxyHead, ct).ConfigureAwait(false);
                // رد الـ Proxy يُنقل كما هو (بما فيه 407): المتصفح هو من يملك بيانات الاعتماد.
                await PumpUntilOriginClosesAsync(client, upstream, request.Remainder, ct).ConfigureAwait(false);
            }
            return;
        }

        var (origin, failure) = await DirectConnectAsync(route.Host, port, ct).ConfigureAwait(false);
        if (origin is null)
        {
            if (failure == DirectFailure.Blocked) Counters.Reject();
            await TryRespondAsync(client, failure == DirectFailure.Blocked ? 403 : 502, ct).ConfigureAwait(false);
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

    /// <summary>
    /// رأس موجَّه إلى Proxy أعلى: هدف absolute-URI مبني من الاسم المطبَّع (لا من نص الطلب كما وصل)،
    /// وحذف Proxy-Connection/Keep-Alive/Connection، مع **الإبقاء على Proxy-Authorization** لأنها موجهة
    /// إلى الـ Proxy الأعلى نفسه (على المسار المباشر بلا Proxy تُحذف؛ هناك لا مُخاطَب لها).
    /// </summary>
    public static byte[] BuildProxyFormHead(ProxyRequest request, string host, int port, string path)
    {
        ArgumentNullException.ThrowIfNull(request);
        var authority = port == 80 ? host : host + ":" + port.ToString(CultureInfo.InvariantCulture);
        var target = "http://" + authority + (string.IsNullOrEmpty(path) ? "/" : path);
        var sb = new StringBuilder();
        sb.Append(request.Method).Append(' ').Append(target).Append(' ').Append(request.Version).Append("\r\n");
        foreach (var (name, value) in request.Headers)
        {
            if (name.Equals("Proxy-Connection", StringComparison.OrdinalIgnoreCase)) continue;
            if (name.Equals("Connection", StringComparison.OrdinalIgnoreCase)) continue;
            if (name.Equals("Keep-Alive", StringComparison.OrdinalIgnoreCase)) continue;
            sb.Append(name).Append(": ").Append(value).Append("\r\n");
        }
        sb.Append("Connection: close\r\n\r\n");
        return Encoding.Latin1.GetBytes(sb.ToString());
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

    /// <summary>
    /// حل الاسم، رفض النتيجة إن كان **أي** عنوان منها محظورًا (نفس قاعدة docs/protocol.md القسم 6 الخطوة 6 على المضيف)،
    /// ثم Socket.ConnectAsync(IPAddress[], port) بالقائمة المفحوصة فقط وبمهلة 5 ثوانٍ للعملية كلها.
    /// <para>
    /// الرفض يقع **بعد** الحل و**قبل** أي اتصال: اسم يحل إلى loopback أو عنوان خاص لا يُفتح إليه مقبس أصلًا.
    /// الاتصال بالقائمة لا بالاسم يغلق DNS rebinding بين الفحص والاتصال.
    /// </para>
    /// </summary>
    private async Task<(SocketStream? Stream, DirectFailure Failure)> DirectConnectAsync(string host, int port, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(_options.DirectConnectTimeout);
        Socket? socket = null;
        try
        {
            var addresses = await _options.Resolver.ResolveAsync(host, cts.Token).ConfigureAwait(false);
            addresses = addresses.Where(a => a is not null && a.AddressFamily is AddressFamily.InterNetwork or AddressFamily.InterNetworkV6).ToArray();
            if (addresses.Length == 0) return (null, DirectFailure.Unreachable);
            if (addresses.Any(_isBlocked)) return (null, DirectFailure.Blocked);
            socket = CreateSocket(addresses);
            await socket.ConnectAsync(addresses, port, cts.Token).ConfigureAwait(false);
            return (new SocketStream(socket), DirectFailure.None);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            socket?.Dispose();
            throw;
        }
        catch (Exception)
        {
            socket?.Dispose();
            return (null, DirectFailure.Unreachable);
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
