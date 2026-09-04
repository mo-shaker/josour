using System.Net;
using System.Net.Sockets;
using RouteBridge.Core.Allowlist;
using RouteBridge.Core.Net;
using RouteBridge.Tunnel.Mux;

namespace RouteBridge.Egress;

/// <summary>نتيجة سياسة الخروج: stream إلى الموقع (يملك المقبس، يدعم الإغلاق النصفي) أو سبب OPEN_FAIL.</summary>
public sealed class EgressResult
{
    private EgressResult(Stream? stream, OpenFailReason? reason, string? normalizedHost, IPAddress? connectedTo)
    {
        Stream = stream;
        Reason = reason;
        NormalizedHost = normalizedHost;
        ConnectedTo = connectedTo;
    }

    public Stream? Stream { get; }
    public OpenFailReason? Reason { get; }
    /// <summary>الاسم بعد التطبيع (متاح من الخطوة 2 فصاعدًا حتى عند الرفض).</summary>
    public string? NormalizedHost { get; }
    public IPAddress? ConnectedTo { get; }
    public bool IsOk => Stream is not null;

    public static EgressResult Ok(Stream stream, string normalizedHost, IPAddress connectedTo) => new(stream, null, normalizedHost, connectedTo);
    public static EgressResult Fail(OpenFailReason reason, string? normalizedHost = null) => new(null, reason, normalizedHost, null);
}

public sealed class EgressPolicyOptions
{
    public static readonly IReadOnlyList<int> DefaultAllowedPorts = new[] { 80, 443 };

    public IReadOnlyList<int> AllowedPorts { get; init; } = DefaultAllowedPorts;
    public TimeSpan DnsTimeout { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(10);
    public IHostResolver Resolver { get; init; } = DnsHostResolver.Instance;

    /// <summary>عناوين المضيف نفسه (واجهاته وبواباته) وعنوانه العام كما يراه الخادم؛ كلها محظورة.</summary>
    public IReadOnlyList<IPAddress> LocalAddresses { get; init; } = Array.Empty<IPAddress>();

    /// <summary>
    /// فحص الحظر لعنوان واحد. الافتراضي IpRangePolicy.IsBlocked(address, LocalAddresses).
    /// يُستبدل في اختبارات E2E فقط (للسماح بـ 127.0.0.1 داخل العملية).
    /// </summary>
    public Func<IPAddress, bool>? AddressBlocker { get; init; }
}

/// <summary>
/// سياسة الخروج على المضيف حسب docs/protocol.md القسم 6 بالترتيب: (1) IP حرفي → ip_literal، (2) تطبيع، (3) القائمة → not_allowed،
/// (4) المنفذ → port_not_allowed، (5) DNS مرة واحدة بمهلة 5 ث → dns_failed، (6) أي عنوان محظور → private_ip،
/// (7) Socket.ConnectAsync(IPAddress[], port) بالقائمة المفحوصة فقط بمهلة 10 ث → connect_failed.
/// </summary>
public sealed class EgressPolicy
{
    private readonly IAllowlist _allowlist;
    private readonly EgressPolicyOptions _options;
    private readonly Func<IPAddress, bool> _isBlocked;

    public EgressPolicy(IAllowlist allowlist, EgressPolicyOptions? options = null)
    {
        _allowlist = allowlist ?? throw new ArgumentNullException(nameof(allowlist));
        _options = options ?? new EgressPolicyOptions();
        var locals = _options.LocalAddresses;
        _isBlocked = _options.AddressBlocker ?? (address => IpRangePolicy.IsBlocked(address, locals));
    }

    public IAllowlist Allowlist => _allowlist;

    /// <summary>الخطوات 1-4 فقط (بلا I/O). يعيد null عند القبول مع الاسم المطبَّع.</summary>
    public OpenFailReason? Evaluate(string host, int port, out string? normalizedHost)
    {
        normalizedHost = null;
        if (string.IsNullOrWhiteSpace(host)) return OpenFailReason.NotAllowed;
        if (IpRangePolicy.IsIpLiteral(host)) return OpenFailReason.IpLiteral;

        string normalized;
        try { normalized = AllowlistMatcher.NormalizeHost(host); }
        catch (ArgumentException) { return OpenFailReason.NotAllowed; }
        normalizedHost = normalized;
        if (port is < 1 or > 65535) return OpenFailReason.PortNotAllowed;

        if (_allowlist.IsAllowed(normalized, port, _options.AllowedPorts)) return null;
        // الاسم مطابق لكن المنفذ ليس ضمن allowed_ports ولا قيد المدخل → port_not_allowed؛ وإلا not_allowed.
        var hostMatched = _allowlist.Entries.Any(entry => AllowlistMatcher.HostMatches(normalized, entry));
        return hostMatched ? OpenFailReason.PortNotAllowed : OpenFailReason.NotAllowed;
    }

    /// <summary>الخطوات 1-7 كاملة. لا يرمي إلا عند الإلغاء الخارجي.</summary>
    public async Task<EgressResult> OpenAsync(string host, int port, CancellationToken ct)
    {
        var early = Evaluate(host, port, out var normalized);
        if (early is { } reason) return EgressResult.Fail(reason, normalized);
        var target = normalized!;

        IPAddress[] addresses;
        try
        {
            using var dnsCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            dnsCts.CancelAfter(_options.DnsTimeout);
            addresses = await _options.Resolver.ResolveAsync(target, dnsCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return EgressResult.Fail(OpenFailReason.DnsFailed, target);
        }

        addresses = addresses.Where(a => a is not null && a.AddressFamily is AddressFamily.InterNetwork or AddressFamily.InterNetworkV6).ToArray();
        if (addresses.Length == 0) return EgressResult.Fail(OpenFailReason.DnsFailed, target);
        if (addresses.Any(_isBlocked)) return EgressResult.Fail(OpenFailReason.PrivateIp, target);

        var socket = CreateSocket(addresses);
        try
        {
            using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            connectCts.CancelAfter(_options.ConnectTimeout);
            // بالقائمة المفحوصة فقط، لا بالاسم (يغلق DNS rebinding وHappy Eyeballs على أسماء غير مفحوصة).
            await socket.ConnectAsync(addresses, port, connectCts.Token).ConfigureAwait(false);
            var remote = socket.RemoteEndPoint as IPEndPoint;
            var connectedTo = remote?.Address ?? addresses[0];
            if (connectedTo.IsIPv4MappedToIPv6) connectedTo = connectedTo.MapToIPv4();
            return EgressResult.Ok(new SocketStream(socket), target, connectedTo);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            socket.Dispose();
            throw;
        }
        catch (Exception)
        {
            socket.Dispose();
            return EgressResult.Fail(OpenFailReason.ConnectFailed, target);
        }
    }

    private static Socket CreateSocket(IPAddress[] addresses)
    {
        var needV6 = addresses.Any(a => a.AddressFamily == AddressFamily.InterNetworkV6);
        var needV4 = addresses.Any(a => a.AddressFamily == AddressFamily.InterNetwork);
        if (needV6 || !needV4)
        {
            try
            {
                return new Socket(AddressFamily.InterNetworkV6, SocketType.Stream, ProtocolType.Tcp) { DualMode = needV4 };
            }
            catch (SocketException) when (needV4)
            {
                // لا IPv6 على الجهاز؛ نسقط إلى v4
            }
        }
        return new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
    }
}
