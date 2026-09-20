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
    /// <summary>The host's public IP as the server sees it; it appears on the check page.</summary>
    public string PeerPublicIp { get; init; } = string.Empty;
    /// <summary>null = no tunnel (everything direct; the self-hosted mode in the Spike tool).</summary>
    public IMuxConnection? Mux { get; init; }
    public IHostResolver Resolver { get; init; } = DnsHostResolver.Instance;
    public IOwnerPidChecker OwnerPidChecker { get; init; } = PermissiveOwnerPidChecker.Instance;
    /// <summary>null = no owner check (every local connection is accepted).</summary>
    public IBrowserSession? Browser { get; init; }
    /// <summary>
    /// The blocked check for one address on the direct path after the name is resolved. The default is <see cref="IpRangePolicy.IsBlocked(IPAddress, IEnumerable{IPAddress}?)"/>:
    /// a name that resolves to loopback or a private address is not connected to, so the local proxy does not become a bridge towards whatever this machine is listening on
    /// (the tunnel's own listener, the relay's port, services on 127.0.0.1). Replaced in in-process tests only, to permit loopback.
    /// </summary>
    public Func<IPAddress, bool>? AddressBlocker { get; init; }
    /// <summary>
    /// When a Browser is present: is a connection whose owner cannot be determined refused? **The default is `true` on every system** (fail-closed).
    /// <para>
    /// The default used to be `OperatingSystem.IsWindows()`, that is, "refuse on Windows and accept everywhere else". And with
    /// <see cref="PermissiveOwnerPidChecker"/> — which is all that exists off Windows — that meant the local proxy
    /// **accepted every process on the machine**, not the work browser alone. That was not a decision taken but the side effect of a default,
    /// and it silently dropped acceptance criterion 10 on any system that is not Windows.
    /// </para>
    /// <para>
    /// Whoever wants "accept everything" must say so explicitly with `false` — as the in-process tests and the Spike tool do. The difference
    /// between a declared choice and a silent default is the whole difference here.
    /// </para>
    /// </summary>
    public bool? RejectUnknownOwner { get; init; }
    public TimeSpan DirectConnectTimeout { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan RequestHeadTimeout { get; init; } = TimeSpan.FromSeconds(30);
    /// <summary>
    /// The system proxy for the **direct** path only (plan 8.5). The default is the machine's settings:
    /// WinHTTP/WinINET on Windows and the environment variables elsewhere, with their bypass list.
    /// The allowed path never goes through here — it goes over the mux to the host.
    /// <see cref="NoSystemProxy.Instance"/> is injected in the tests so they do not depend on the machine's settings.
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
    /// <summary>How many direct requests went through the system proxy rather than a raw socket (diagnostics for corporate networks).</summary>
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
/// A local proxy on 127.0.0.1:0 for the work browser (docs/protocol.md and plan 7.5):
/// - An owner check for every connection (the PID inside the browser's Job Object).
/// - CONNECT host:port (ws/wss too, and [IPv6]:port): an address literal/local -> 403; allowed -> OPEN through the tunnel, and 200 only after OPEN_OK
///   (OPEN_FAIL -> a truthful 403/502/503, and not_allowed during a list-version divergence -> falling back to direct); not allowed -> direct TCP (5 s).
/// - An http:// absolute-URI: check.josour -> the check page; allowed -> a 307 to https://; otherwise -> one request per connection in origin-form
///   with Connection: close, pumped until the origin closes.
/// One connection = one request, always; the local replies carry Connection: close.
/// </summary>
public sealed class ConnectProxyServer : IAsyncDisposable
{
    /// <summary>Why the direct connection failed: the address policy (403) or being unreachable (502).</summary>
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

    /// <summary>Raised on every arrival of the check page with its arrival time; FirstProbeHitAt keeps the first.</summary>
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

    /// <summary>Cleanup step 1 (docs/protocol.md section 7): no new connections; the ones under way continue until StopAsync.</summary>
    public void StopAccepting()
    {
        if (Interlocked.Exchange(ref _accepting, 0) == 0 && _acceptLoop is null) return;
        try { _listener.Close(); } catch { /* ignore */ }
    }

    public async Task StopAsync()
    {
        if (Interlocked.Exchange(ref _stopped, 1) != 0) return;
        StopAccepting();
        _cts.Cancel();
        if (_acceptLoop is not null) { try { await _acceptLoop.ConfigureAwait(false); } catch { /* ignore */ } }
        try { await Task.WhenAll(_connections.Keys).ConfigureAwait(false); } catch { /* the handlers swallow their own errors */ }
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
            try { socket.Close(0); } catch { /* ignore */ }
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
            // not_allowed during the list-version divergence window: the host knows the newer one; we fall back to direct (ADR-0004).
        }

        // A corporate network with a mandatory proxy: the direct path passes CONNECT to the system proxy (plan 8.5).
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
    /// CONNECT through the system proxy. 2xx -> 200 to the browser and pumping; 407 -> relayed to the browser with <c>Proxy-Authenticate</c>
    /// so it retries with <c>Proxy-Authorization</c> (Basic/Digest in one round trip); anything else -> a truthful 502.
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
        // Bytes that arrived after the proxy's reply head belong to the tunnel itself and must reach the browser.
        if (result.Remainder.Length > 0)
        {
            await client.WriteAsync(result.Remainder, ct).ConfigureAwait(false);
            await client.FlushAsync(ct).ConfigureAwait(false);
        }
        await PumpAsync(client, tunnel, request.Remainder, ct).ConfigureAwait(false);
    }

    /// <summary>The system proxy for this destination, or null if none is configured or it is in the bypass list. It does not throw.</summary>
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
            return null; // a name that cannot build a Uri: we carry on directly
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
            // An absolute-URI as the browser sends it to a proxy
        }
        else if (request.Target.StartsWith('/') && request.Header("Host") is { Length: > 0 } hostHeader
                 && HttpRequestParser.TryParseAuthority(hostHeader, 80, out host, out port))
        {
            path = request.Target; // origin-form (curl straight at the port, or the check page with a Host header)
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
            // No plaintext bytes through the tunnel: a local redirect to https
            var location = port == 80 ? $"https://{route.Host}{path}" : $"https://{route.Host}:{port}{path}";
            await TryRespondAsync(client, 307, ct, ("Location", location)).ConfigureAwait(false);
            return;
        }

        // A corporate network with a mandatory proxy: the request goes to the system proxy in absolute-URI form (plan 8.5).
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
                // The proxy's reply is relayed as it is (407 included): the browser is what holds the credentials.
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
            // One request per connection: we pump the body if there is one and relay the reply until the origin closes, then close towards the browser.
            await PumpUntilOriginClosesAsync(client, origin, request.Remainder, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// A head aimed at an upstream proxy: an absolute-URI target built from the normalised name (not from the request's text as it arrived),
    /// with Proxy-Connection/Keep-Alive/Connection removed, and **Proxy-Authorization kept**, because it is addressed
    /// to that upstream proxy itself (on the direct path with no proxy it is removed; there is nobody there for it to address).
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

    /// <summary>An absolute-URI -> origin-form; Proxy-Connection/Proxy-Authorization/Keep-Alive removed; Connection: close.</summary>
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
        try { ProbeHit?.Invoke(now); } catch { /* the listener is responsible for its own errors */ }
        var body = ProbePage.HtmlBytes(_options.PeerPublicIp);
        await TryRespondAsync(client, 200, ct, body, "text/html; charset=utf-8", ("Cache-Control", "no-store")).ConfigureAwait(false);
    }

    // ---------- direct ----------

    /// <summary>
    /// Resolving the name, refusing the result if **any** of its addresses is blocked (the same rule as docs/protocol.md section 6 step 6 on the host),
    /// then Socket.ConnectAsync(IPAddress[], port) using the checked list only, with a 5-second timeout for the whole operation.
    /// <para>
    /// The refusal happens **after** the resolution and **before** any connection: a name that resolves to loopback or a private address has no socket opened to it at all.
    /// Connecting to the list rather than to the name closes DNS rebinding between the check and the connection.
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

    /// <summary>The reply is copied until the origin closes; the request then ends and we do not wait on the browser (it is closed towards after returning).</summary>
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
            catch { /* the browser closed, or it was cancelled */ }
        }, CancellationToken.None);
        try { await StreamPump.CopyAsync(origin, client, cts.Token).ConfigureAwait(false); }
        catch { /* the origin dropped the connection, or the browser closed */ }
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
            // The browser closed; there is nothing to do
        }
    }
}
