using System.Net;
using Josour.Tunnel;
using Josour.Tunnel.Mux;

namespace Josour.Egress;

/// <summary>
/// جانب المضيف من <see cref="TunnelSession"/>: يبني <see cref="EgressPolicy"/> من سياق الجلسة (القائمة، المنافذ، المحلل،
/// عناوين الجهاز المحظورة) ويربط <see cref="OpenHandler"/> بـ acceptor الـ Mux، ويكشف العدّادات والنطاقات.
///
/// لماذا مصنع لا إنشاء مباشر داخل TunnelSession: <c>Josour.Egress</c> يعتمد على <c>Josour.Tunnel</c>
/// (ADR-0006: الـ Mux يسكن هناك)، فلا يمكن للاتجاه أن ينعكس. المسار C يمرر <c>EgressTunnelAdapter.Create</c> كما هي.
/// </summary>
public sealed class EgressTunnelAdapter : ITunnelEgress
{
    private readonly OpenHandler _handler;

    public EgressTunnelAdapter(OpenHandler handler)
        => _handler = handler ?? throw new ArgumentNullException(nameof(handler));

    /// <summary>الاستعمال الإنتاجي: <c>HostEgress = EgressTunnelAdapter.Create</c>.</summary>
    public static ITunnelEgress Create(TunnelEgressContext context) => Create(context, null, null);

    /// <param name="addressBlocker">
    /// استبدال فحص الحظر لعنوان واحد؛ لاختبارات E2E داخل العملية فقط (للسماح بـ 127.0.0.1). null = سياسة IpRangePolicy.
    /// </param>
    /// <param name="limiter">
    /// حدود streams مخصّصة؛ null = <see cref="TunnelEgressContext.MaxConcurrentStreams"/> (المرافق للنافذة
    /// المشتقة من الـ RTT: 256 أو 128 أو 64) و50 فتحًا في الثانية كما هي في العقد.
    /// </param>
    public static ITunnelEgress Create(TunnelEgressContext context, Func<IPAddress, bool>? addressBlocker, StreamLimiter? limiter = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        var policy = new EgressPolicy(context.Allowlist, new EgressPolicyOptions
        {
            AllowedPorts = context.AllowedPorts,
            Resolver = context.Resolver,
            LocalAddresses = context.BlockedLocalAddresses,
            AddressBlocker = addressBlocker,
        });
        return new EgressTunnelAdapter(new OpenHandler(policy, limiter ?? new StreamLimiter(context.MaxConcurrentStreams)));
    }

    /// <summary>المعالج الأصلي (عدّادات الفتح الناجح والفاشل، الحدود) لمن يحتاج تفصيلًا أدق من الواجهة.</summary>
    public OpenHandler Handler => _handler;

    public long BytesUp => _handler.Counter.Up;
    public long BytesDown => _handler.Counter.Down;
    public IReadOnlyCollection<string> Domains => _handler.Domains.Snapshot();

    public void Attach(IMuxAcceptor acceptor) => _handler.Attach(acceptor);

    /// <summary>لا موارد خاصة: الـ streams تملكها القنوات ويغلقها الـ Mux عند إغلاق النفق.</summary>
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
