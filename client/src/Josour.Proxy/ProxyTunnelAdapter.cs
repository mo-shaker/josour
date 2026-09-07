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
            OwnerPidChecker = ownerPidChecker ?? PermissiveOwnerPidChecker.Instance,
            AddressBlocker = addressBlocker,
            SystemProxy = systemProxy ?? SystemProxyResolver.Default,
        };
        return new ProxyTunnelAdapter(new ConnectProxyServer(options));
    }

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
