using System.Net;
using Josour.Core.Browser;
using Josour.Core.Net;
using Josour.Tunnel;

namespace Josour.Proxy;

/// <summary>
/// جانب Guest من <see cref="TunnelSession"/>: يبني <see cref="ConnectProxyServer"/> فوق الـ Mux ويكشف منفذه ورابط صفحة
/// الفحص وإشارة الوصول إليها. الجلسة تكشف هذه الثلاثة للتطبيق في <c>Proxy</c> و<c>ProbeSeen</c> ليشغّل المتصفح ويكشف
/// <c>browser_not_proxied</c>.
///
/// لماذا مصنع: <c>Josour.Proxy</c> يعتمد على <c>Josour.Tunnel</c> (الـ Mux)، فلا يمكن للاتجاه أن ينعكس.
/// المسار C يمرر <c>ProxyTunnelAdapter.Create</c>، أو <c>ctx =&gt; ProxyTunnelAdapter.Create(ctx, browser)</c> ليفعّل فحص PID المالك.
/// </summary>
public sealed class ProxyTunnelAdapter : ITunnelProxy
{
    private readonly ConnectProxyServer _server;

    public ProxyTunnelAdapter(ConnectProxyServer server)
    {
        _server = server ?? throw new ArgumentNullException(nameof(server));
        _server.ProbeHit += OnProbeHit;
    }

    public IReadOnlyDictionary<string, long> Counters => new Dictionary<string, long>(StringComparer.Ordinal)
    {
        ["accepted"] = _server.Counters.Accepted,
        ["rejected_by_owner"] = _server.Counters.RejectedByOwner,
        ["tunneled"] = _server.Counters.Tunneled,
        ["direct"] = _server.Counters.DirectConnects,
        ["direct_http"] = _server.Counters.DirectHttpRequests,
        ["rejected"] = _server.Counters.Rejected,
        ["probe_hits"] = _server.Counters.ProbeHits,
        ["errors"] = _server.Counters.Errors,
        ["via_system_proxy"] = _server.Counters.ViaSystemProxy,
    };

    /// <summary>الاستعمال الإنتاجي بلا فحص مالك: <c>GuestProxy = ProxyTunnelAdapter.Create</c>.</summary>
    public static ITunnelProxy Create(TunnelProxyContext context) => Create(context, null);

    /// <param name="browser">جلسة المتصفح لفحص PID المالك؛ null = كل اتصال محلي مقبول (اختبارات وأداة Spike).</param>
    /// <param name="ownerPidChecker">فاحص PID مخصّص؛ null = الافتراضي.</param>
    /// <param name="addressBlocker">
    /// استبدال فحص حظر العناوين على المسار المباشر؛ لاختبارات داخل العملية فقط (للسماح بـ 127.0.0.1).
    /// null = سياسة <c>IpRangePolicy</c> (نظير <c>EgressTunnelAdapter.Create</c> على المضيف).
    /// </param>
    /// <param name="systemProxy">
    /// Proxy النظام للمسار المباشر (الخطة 8.5). null = إعدادات الجهاز الحقيقية.
    /// الاختبارات داخل العملية تمرر <see cref="NoSystemProxy.Instance"/> كي لا تتأثر بإعدادات جهاز المطوّر.
    /// </param>
    public static ITunnelProxy Create(
        TunnelProxyContext context,
        IBrowserSession? browser,
        IOwnerPidChecker? ownerPidChecker = null,
        Func<IPAddress, bool>? addressBlocker = null,
        ISystemProxyResolver? systemProxy = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        var options = new ConnectProxyOptions
        {
            Allowlist = context.Allowlist,
            AllowedPorts = context.AllowedPorts,
            Resolver = context.Resolver,
            PeerPublicIp = context.PeerPublicIp,
            Mux = context.Mux,
            Browser = browser,
            // The default must match the platform, not the tests. PermissiveOwnerPidChecker answers
            // "I don't know" to every question, and on Windows an unknown owner is refused - so wiring a
            // browser without also wiring a checker refused every connection the browser made, which is
            // exactly what shipped: accepted=33 rejected_by_owner=33 and a session that could not browse.
            OwnerPidChecker = ownerPidChecker ?? DefaultOwnerPidChecker(),
            AddressBlocker = addressBlocker,
            SystemProxy = systemProxy ?? SystemProxyResolver.Default,
        };
        return new ProxyTunnelAdapter(new ConnectProxyServer(options));
    }

    /// <summary>
    /// The checker that can actually answer on this platform (<see cref="OwnerPidCheckers.ForCurrentPlatform"/>).
    /// Only Windows has one. Elsewhere this returns the permissive checker and the proxy then refuses to start,
    /// which is the intended outcome: a guest role that cannot tell the work browser from the rest of the machine
    /// is not a guest role, and pretending otherwise would hand the host's address to every program on it.
    /// </summary>
    private static IOwnerPidChecker DefaultOwnerPidChecker() => OwnerPidCheckers.ForCurrentPlatform();

    /// <summary>الخادم الأصلي (العدّادات التفصيلية، وقت أول وصول لصفحة الفحص).</summary>
    public ConnectProxyServer Server => _server;

    public int Port => _server.Port;
    public string ProbeUrl => ProbePage.Url;

    public event Action? ProbeSeen;

    public void Start() => _server.Start();
    public void StopAccepting() => _server.StopAccepting();
    public Task StopAsync() => _server.StopAsync();

    public async ValueTask DisposeAsync()
    {
        _server.ProbeHit -= OnProbeHit;
        await _server.DisposeAsync().ConfigureAwait(false);
    }

    private void OnProbeHit(DateTimeOffset _)
    {
        try { ProbeSeen?.Invoke(); } catch { /* المستمع مسؤول عن أخطائه */ }
    }
}
