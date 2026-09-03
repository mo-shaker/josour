using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using RouteBridge.Core.Tunnel;
using RouteBridge.Tunnel.Auth;
using RouteBridge.Tunnel.Candidates;
using RouteBridge.Tunnel.Certificates;
using RouteBridge.Tunnel.Tls;

namespace RouteBridge.Tunnel;

/// <summary>اتصال مصادَق (TLS + AUTH1/AUTH2 مكتملان). الـ Stream هو SslStream يملك المقبس.</summary>
public sealed record AuthenticatedConnection(Stream Stream, CandidateType WinnerType, string TlsVersion, bool Inbound, string RemoteDescription) : IAsyncDisposable
{
    public ValueTask DisposeAsync() => Stream.DisposeAsync();
}

public sealed record SymmetricConnectOutcome(TunnelConnectResult Result, AuthenticatedConnection? Connection, IReadOnlyDictionary<string, object?> Diagnostics);

/// <summary>
/// تنسيق docs/protocol.md القسم 2 الخطوات 3-6 لكلا الدورين: قبول الاتصالات الواردة (TLS Server بشهادتنا) والاتصال بكل مرشحي
/// الطرف الآخر بالتوازي (TLS Client مثبّت على بصمته)، ثم المصادقة حسب الدور المنطقي. أول اتصال يجتاز المصادقة يفوز؛
/// الباقي يُغلق ويُوقَف المستمع.
///
/// من يقرر الفائز: المضيف هو المرجع (session.connected). لتفادي أن يحتفظ كل طرف باتصال مختلف، المضيف لا يرسل AUTH2 إلا على
/// أول اتصال يجتاز AUTH1 ويغلق ما عداه بلا رد (وهو ما يسمح به القسم 4). وهكذا لا يرى Guest سوى AUTH2 واحدة، فاتصاله
/// "الأول المصادَق" هو نفسه اتصال المضيف. الأسبوع 4 يضيف مصافحة تأكيد صريحة إن أظهرت التجارب الحقيقية حاجة إليها.
/// </summary>
public sealed class SymmetricConnector
{
    public static readonly TimeSpan DialTimeout = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan TlsTimeout = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan AuthTimeout = TimeSpan.FromSeconds(5);

    private readonly SessionMaterial _material;
    private readonly SessionCertificate _certificate;
    private readonly TunnelListener _listener;
    private readonly ITunnelTransport _transport;
    private readonly IReadOnlyList<CandidateEndpoint> _ourCandidates;

    private readonly List<Attempt> _attempts = new();
    private readonly object _gate = new();
    private int _claimed;
    private int _started;

    public SymmetricConnector(SessionMaterial material, SessionCertificate ourCertificate, TunnelListener listener, ITunnelTransport transport, IReadOnlyList<CandidateEndpoint>? ourCandidates = null)
    {
        _material = material ?? throw new ArgumentNullException(nameof(material));
        _certificate = ourCertificate ?? throw new ArgumentNullException(nameof(ourCertificate));
        _listener = listener ?? throw new ArgumentNullException(nameof(listener));
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _ourCandidates = ourCandidates ?? Array.Empty<CandidateEndpoint>();
    }

    /// <summary>يُستدعى مرة واحدة. عند الانتهاء يكون المستمع متوقفًا وكل المحاولات الأخرى مغلقة.</summary>
    public async Task<SymmetricConnectOutcome> ConnectAsync(PeerEndpointInfo peer, TimeSpan timeout, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(peer);
        if (Interlocked.Exchange(ref _started, 1) != 0) throw new InvalidOperationException("ConnectAsync may be called only once");

        byte[] peerFp;
        try { peerFp = Convert.FromHexString(peer.CertFingerprintSha256Hex); }
        catch (FormatException e) { throw new ArgumentException("peer certificate fingerprint is not valid hex", nameof(peer), e); }
        if (peerFp.Length != TlsChannel.FingerprintLength) throw new ArgumentException("peer certificate fingerprint must be 32 bytes", nameof(peer));
        var ourFp = _certificate.FingerprintSha256;

        var clock = Stopwatch.StartNew();
        var winner = new TaskCompletionSource<(AuthenticatedConnection Connection, long Ms)>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);

        await _listener.StartAsync((socket, lct) => HandleInboundAsync(socket, peerFp, ourFp, winner, clock, lct), cts.Token).ConfigureAwait(false);
        var dials = peer.Candidates.Select(c => DialAsync(c, peerFp, ourFp, winner, clock, cts.Token)).ToArray();

        try
        {
            await winner.Task.WaitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // مهلة أو إلغاء خارجي
        }

        cts.Cancel();
        await _listener.StopAsync().ConfigureAwait(false);
        try { await Task.WhenAll(dials).ConfigureAwait(false); } catch { /* المحاولات تبتلع أخطاءها */ }

        var elapsed = (int)Math.Min(clock.ElapsedMilliseconds, int.MaxValue);
        if (winner.Task.IsCompletedSuccessfully)
        {
            var (connection, ms) = winner.Task.Result;
            var result = new TunnelConnectResult(true, connection.WinnerType, (int)Math.Min(ms, int.MaxValue), connection.TlsVersion, null);
            return new SymmetricConnectOutcome(result, connection, BuildDiagnostics(timeout, elapsed, connection));
        }

        var reason = ct.IsCancellationRequested ? "cancelled" : "timeout";
        var failed = new TunnelConnectResult(false, null, elapsed, null, reason);
        return new SymmetricConnectOutcome(failed, null, BuildDiagnostics(timeout, elapsed, null));
    }

    // ---------- inbound ----------

    private async Task HandleInboundAsync(Socket socket, byte[] peerFp, byte[] ourFp, TaskCompletionSource<(AuthenticatedConnection, long)> winner, Stopwatch clock, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var remote = socket.RemoteEndPoint as IPEndPoint;
        var local = socket.LocalEndPoint as IPEndPoint;
        var remoteAddress = remote?.Address is { IsIPv4MappedToIPv6: true } mapped ? mapped.MapToIPv4() : remote?.Address;
        var attempt = new Attempt("inbound", ClassifyInbound(local), remoteAddress?.ToString() ?? "?", remote?.Port ?? 0);
        Record(attempt);
        try
        {
            Stream raw;
            try { raw = new NetworkStream(socket, ownsSocket: true); }
            catch { socket.Dispose(); throw; }
            await AuthenticateAsync(raw, inbound: true, attempt, peerFp, ourFp, winner, clock, ct).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            attempt.Error = Describe(e);
        }
        finally
        {
            attempt.Ms = sw.ElapsedMilliseconds;
        }
    }

    // ---------- dial ----------

    private async Task DialAsync(CandidateEndpoint candidate, byte[] peerFp, byte[] ourFp, TaskCompletionSource<(AuthenticatedConnection, long)> winner, Stopwatch clock, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var attempt = new Attempt("dial", candidate.Type, candidate.Ip, candidate.Port) { Stage = "connect" };
        Record(attempt);
        try
        {
            var raw = await _transport.ConnectAsync(candidate, DialTimeout, ct).ConfigureAwait(false);
            await AuthenticateAsync(raw, inbound: false, attempt, peerFp, ourFp, winner, clock, ct).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            attempt.Error = Describe(e);
        }
        finally
        {
            attempt.Ms = sw.ElapsedMilliseconds;
        }
    }

    // ---------- shared ----------

    private async Task AuthenticateAsync(Stream raw, bool inbound, Attempt attempt, byte[] peerFp, byte[] ourFp, TaskCompletionSource<(AuthenticatedConnection, long)> winner, Stopwatch clock, CancellationToken ct)
    {
        attempt.Stage = "tls";
        var tls = inbound
            ? await TlsChannel.AuthenticateAsServerAsync(raw, _certificate.Certificate, TlsTimeout, ct).ConfigureAwait(false)
            : await TlsChannel.AuthenticateAsClientAsync(raw, peerFp, TlsTimeout, ct).ConfigureAwait(false);

        // listener_cert_fp = شهادة من يعمل TLS Server في هذا الاتصال: نحن عند القبول، والطرف الآخر عند الاتصال.
        var listenerFp = inbound ? ourFp : peerFp;
        attempt.Stage = "auth";
        try
        {
            if (_material.Role == TunnelRole.Host)
            {
                var auth1 = await AuthHandshake.VerifyAuth1Async(tls.Stream, _material, listenerFp, AuthTimeout, ct).ConfigureAwait(false);
                if (!TryClaim()) throw new SupersededException();
                try
                {
                    await AuthHandshake.SendAuth2Async(tls.Stream, _material, listenerFp, auth1, AuthTimeout, ct).ConfigureAwait(false);
                }
                catch
                {
                    Volatile.Write(ref _claimed, 0);
                    throw;
                }
            }
            else
            {
                await AuthHandshake.SendAuth1AndVerifyAuth2Async(tls.Stream, _material, listenerFp, AuthTimeout, ct).ConfigureAwait(false);
                if (!TryClaim()) throw new SupersededException();
            }

            var connection = new AuthenticatedConnection(tls.Stream, attempt.Type ?? CandidateType.Public, tls.TlsVersion, inbound, $"{attempt.Ip}:{attempt.Port}");
            attempt.Stage = "ok";
            if (!winner.TrySetResult((connection, clock.ElapsedMilliseconds)))
                throw new SupersededException();
        }
        catch
        {
            await tls.Stream.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private bool TryClaim() => Interlocked.CompareExchange(ref _claimed, 1, 0) == 0;

    private void Record(Attempt attempt)
    {
        lock (_gate) _attempts.Add(attempt);
    }

    /// <summary>
    /// تصنيف الاتصال الوارد حسب العنوان المحلي الذي وصل إليه: يطابق مرشحينا lan/v6؛ وإلا فالاتصال جاء عبر NAT
    /// حيث لا يمكن التمييز بين upnp وpublic من جهة المستقبِل، فنفضّل upnp إن كان لدينا تعيين.
    /// </summary>
    private CandidateType? ClassifyInbound(IPEndPoint? local)
    {
        if (local is null) return null;
        var address = local.Address.IsIPv4MappedToIPv6 ? local.Address.MapToIPv4() : local.Address;
        var text = address.ToString();
        foreach (var c in _ourCandidates)
            if (c.Type is CandidateType.Lan or CandidateType.V6 && c.Ip == text) return c.Type;
        if (address.AddressFamily == AddressFamily.InterNetworkV6 && LocalNetwork.IsGlobalIPv6(address)) return CandidateType.V6;
        if (address.AddressFamily == AddressFamily.InterNetwork && IPAddress.IsLoopback(address)) return CandidateType.Lan;
        if (_ourCandidates.Any(c => c.Type == CandidateType.Upnp)) return CandidateType.Upnp;
        return CandidateType.Public;
    }

    private IReadOnlyDictionary<string, object?> BuildDiagnostics(TimeSpan timeout, int elapsedMs, AuthenticatedConnection? connection)
    {
        Attempt[] attempts;
        lock (_gate) attempts = _attempts.ToArray();
        var rows = attempts.Select(a => new Dictionary<string, object?>
        {
            ["direction"] = a.Direction,
            ["type"] = a.Type is null ? null : CandidateTypeNames.ToWire(a.Type.Value),
            ["ip"] = a.Ip,
            ["port"] = a.Port,
            ["ms"] = a.Ms,
            ["stage"] = a.Stage,
            ["error"] = a.Error,
        }).ToList();

        return new Dictionary<string, object?>
        {
            ["role"] = _material.Role == TunnelRole.Host ? "host" : "guest",
            ["timeout_ms"] = (long)timeout.TotalMilliseconds,
            ["elapsed_ms"] = elapsedMs,
            ["listener_port"] = _listener.Port,
            ["inbound_attempts"] = _listener.InboundAttempts,
            ["inbound_rejected"] = _listener.RejectedOverCapacity,
            ["candidates"] = rows,
            ["winner"] = connection is null ? null : $"{(connection.Inbound ? "inbound" : "dial")} {CandidateTypeNames.ToWire(connection.WinnerType)} {connection.RemoteDescription}",
            ["winner_type"] = connection is null ? null : CandidateTypeNames.ToWire(connection.WinnerType),
            ["tls_version"] = connection?.TlsVersion,
        };
    }

    // لا أسرار ولا حمولات في النصوص؛ رسائل الاستثناءات هنا وصفية فقط.
    private static string Describe(Exception e) => e switch
    {
        SupersededException => "superseded",
        OperationCanceledException => "cancelled",
        TimeoutException t => $"timeout: {t.Message}",
        TlsTooOldException => "tls_too_old",
        AuthFailedException a => $"auth_failed: {a.Message}",
        AuthenticationException a => $"tls: {a.Message}",
        SocketException s => $"socket: {s.SocketErrorCode}",
        IOException io when io.InnerException is SocketException s => $"socket: {s.SocketErrorCode}",
        _ => $"{e.GetType().Name}: {e.Message}",
    };

    private sealed class SupersededException : Exception
    {
        public SupersededException() : base("another connection already won") { }
    }

    private sealed class Attempt
    {
        public Attempt(string direction, CandidateType? type, string ip, int port)
        {
            Direction = direction;
            Type = type;
            Ip = ip;
            Port = port;
        }

        public string Direction { get; }
        public CandidateType? Type { get; }
        public string Ip { get; }
        public int Port { get; }
        public long Ms { get; set; }
        public string? Stage { get; set; }
        public string? Error { get; set; }
    }
}
