using System.Net;
using RouteBridge.Core.Allowlist;
using RouteBridge.Core.Net;
using RouteBridge.Core.Tunnel;
using RouteBridge.Tunnel.Candidates;
using RouteBridge.Tunnel.Mux;
using RouteBridge.Tunnel.Transport;

namespace RouteBridge.Tunnel;

/// <summary>
/// مصدر المرشحين ومالك تعيين UPnP. <see cref="CandidateGatherer"/> ينفذه؛ الاختبارات تستبدله بمصدر ثابت.
/// </summary>
public interface ICandidateSource : IAsyncDisposable
{
    /// <summary>هل يوجد تعيين UPnP نشط يجب إزالته عند التنظيف؟</summary>
    bool HasMapping { get; }

    Task<GatherResult> GatherAsync(GatherOptions options, CancellationToken ct);

    /// <summary>خطوة التنظيف 4 (docs/protocol.md القسم 7). لا ترمي؛ false عند الفشل أو غياب التعيين.</summary>
    Task<bool> RemoveMappingAsync(CancellationToken ct);
}

/// <summary>ما تحتاجه سياسة الخروج على المضيف. <see cref="TunnelSession"/> يبنيه ويمرره إلى مصنع الخروج.</summary>
/// <param name="BlockedLocalAddresses">عناوين واجهات المضيف وبواباته وعنوانه العام كما يراه الخادم (docs/protocol.md القسم 6 القاعدة 6).</param>
public sealed record TunnelEgressContext(
    IAllowlist Allowlist,
    IReadOnlyList<int> AllowedPorts,
    IHostResolver Resolver,
    IReadOnlyList<IPAddress> BlockedLocalAddresses,
    IMuxAcceptor Acceptor);

/// <summary>ما يحتاجه الـ Proxy المحلي على جانب Guest. <see cref="TunnelSession"/> يبنيه ويمرره إلى مصنع الـ Proxy.</summary>
/// <param name="PeerPublicIp">IP المضيف العام كما يراه الخادم؛ يظهر في صفحة الفحص.</param>
public sealed record TunnelProxyContext(
    IAllowlist Allowlist,
    IReadOnlyList<int> AllowedPorts,
    IHostResolver Resolver,
    string PeerPublicIp,
    IMuxConnection Mux);

/// <summary>
/// جانب المضيف: سياسة الخروج والعدّادات مربوطة بـ <see cref="IMuxAcceptor"/>.
/// التنفيذ الحقيقي <c>RouteBridge.Egress.EgressTunnelAdapter</c>؛ يُحقن كمصنع لأن Egress يعتمد على Tunnel لا العكس.
/// </summary>
public interface ITunnelEgress : IAsyncDisposable
{
    /// <summary>يسجّل معالج OPEN على الـ acceptor. يُستدعى مرة واحدة.</summary>
    void Attach(IMuxAcceptor acceptor);

    /// <summary>البايتات من Guest إلى المواقع (حمولة صافية، بلا رؤوس النفق).</summary>
    long BytesUp { get; }

    /// <summary>البايتات من المواقع إلى Guest.</summary>
    long BytesDown { get; }

    /// <summary>النطاقات المميزة المطبَّعة لـ session.end.domains.</summary>
    IReadOnlyCollection<string> Domains { get; }
}

/// <summary>
/// جانب Guest: Proxy محلي فوق النفق. التنفيذ الحقيقي <c>RouteBridge.Proxy.ProxyTunnelAdapter</c>
/// (يُحقن كمصنع للسبب نفسه: Proxy يعتمد على Tunnel لا العكس).
/// </summary>
public interface ITunnelProxy : IAsyncDisposable
{
    /// <summary>منفذ الاستماع على 127.0.0.1 (معروف فور الإنشاء، قبل Start).</summary>
    int Port { get; }

    /// <summary>رابط صفحة الفحص الذي يُفتح عليه المتصفح.</summary>
    string ProbeUrl { get; }

    /// <summary>أول وصول لصفحة الفحص (وما بعده).</summary>
    event Action? ProbeSeen;

    void Start();

    /// <summary>خطوة التنظيف 1: لا اتصالات جديدة؛ الجارية تستمر.</summary>
    void StopAccepting();

    /// <summary>إيقاف كامل وانتظار الاتصالات الجارية.</summary>
    Task StopAsync();
}

/// <summary>
/// القطع القابلة للحقن في <see cref="TunnelSession"/>. كل القيم لها افتراضي إنتاجي معقول عدا المصنعين:
/// المضيف يحتاج <see cref="HostEgress"/> والضيف يحتاج <see cref="GuestProxy"/> (إلا إن أُطفئ <see cref="StartGuestProxy"/>).
/// </summary>
public sealed record TunnelSessionOptions
{
    /// <summary>قائمة المواقع المحمّلة من الخادم. الافتراضي قائمة فارغة (كل شيء مباشر على Guest، وكل شيء مرفوض على المضيف).</summary>
    public IAllowlist Allowlist { get; init; } = new AllowlistMatcher(0, Array.Empty<AllowlistEntry>());

    /// <summary>allowed_ports من hello.ack.settings.</summary>
    public IReadOnlyList<int> AllowedPorts { get; init; } = new[] { 80, 443 };

    public IHostResolver Resolver { get; init; } = DnsHostResolver.Instance;

    /// <summary>النقل الخام. Direct اليوم؛ <see cref="RelayTransport"/> إن فتحت بوابة ADR-0003.</summary>
    public ITunnelTransport Transport { get; init; } = new DirectTransport();

    /// <summary>IP هذا الجهاز العام كما يراه الخادم (hello.ack.public_ip): مرشح public، ومحظور في سياسة الخروج.</summary>
    public string? OurPublicIp { get; init; }

    public int ListenPort { get; init; }

    /// <summary>عنوان الربط للمستمع. null = [::]:0 بوضع DualMode (الإنتاج)؛ الاختبارات تربط على loopback.</summary>
    public IPAddress? BindAddress { get; init; }

    public bool EnableUpnp { get; init; } = true;

    /// <summary>عمر تعيين UPnP = المدة المتبقية حتى expires_at + هذه الزيادة (docs/protocol.md القسم 2).</summary>
    public TimeSpan MappingLifetimeSlack { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>مهلة اكتشاف بوابة UPnP في المصدر الافتراضي (الافتراضي 4 ثوانٍ). تُتجاهَل مع مصدر مخصّص.</summary>
    public TimeSpan? UpnpDiscoveryTimeout { get; init; }

    /// <summary>مصدر المرشحين. null = <see cref="CandidateGatherer"/> الحقيقي (Mono.Nat).</summary>
    public Func<ICandidateSource>? CandidateSource { get; init; }

    /// <summary>عناوين هذا الجهاز المحظورة على المضيف. null = تُقرأ من الواجهات عند PrepareAsync.</summary>
    public IReadOnlyList<IPAddress>? LocalAddresses { get; init; }

    public MuxOptions? Mux { get; init; }

    /// <summary>مصنع سياسة الخروج على المضيف (<c>EgressTunnelAdapter.Create</c>).</summary>
    public Func<TunnelEgressContext, ITunnelEgress>? HostEgress { get; init; }

    /// <summary>مصنع الـ Proxy المحلي على Guest (<c>ProxyTunnelAdapter.Create</c>).</summary>
    public Func<TunnelProxyContext, ITunnelProxy>? GuestProxy { get; init; }

    /// <summary>false = لا Proxy على Guest (وضع self-hosted في أداة Spike، واختبارات النفق وحده).</summary>
    public bool StartGuestProxy { get; init; } = true;

    /// <summary>
    /// خطوة التنظيف 2 (docs/protocol.md القسم 7): إغلاق المتصفح المهذب ثم Job Object. النفق لا يملك المتصفح،
    /// فالتطبيق يمرر هنا إغلاقه ليُنفَّذ في مكانه الصحيح من الترتيب: بعد إيقاف قبول الـ Proxy وقبل GOAWAY.
    /// أخطاؤه تُبتلع وتُسجَّل في Diagnostics ولا توقف بقية التنظيف.
    /// </summary>
    public Func<CancellationToken, Task>? CloseBrowserAsync { get; init; }

    /// <summary>مهلة كل خطوة إغلاق في EndAsync حتى لا يعلّق إنهاء الجلسة على طرف ميت.</summary>
    public TimeSpan CleanupStepTimeout { get; init; } = TimeSpan.FromSeconds(5);
}
