using System.Net;
using System.Net.Sockets;

namespace Josour.Tunnel;

/// <summary>
/// عدّاد الاتصالات الواردة التي لم تجتز <c>AUTH1</c> على مستمع النفق (docs/protocol.md القسم 2).
///
/// <para><b>لماذا يوجد:</b> مستمع النفق مفتوح بين <c>session.created</c> و<c>session.connected</c> فقط، وهو الموضع
/// الوحيد الذي يرى فيه النظام محاولة وصول غير مصرَّح بها إلى منفذ الجهاز. الخادم لا يستطيع رصدها بنفسه (لا يمر بها
/// شيء منه)، فيرفعها العميل في <c>POST /api/v1/diagnostics</c> بالمفاتيح المحجوزة في <c>docs/api.md</c>:
/// <c>listener_unauthenticated</c> و<c>listener_port</c> و<c>unauthenticated_peers</c>.</para>
///
/// <para><b>ما يُحفظ:</b> عدّاد وعناوين مصدر مميزة بحد <see cref="MaxPeers"/> فقط. لا حمولات ولا منافذ مصدر ولا أوقات
/// (متطلب الخصوصية في القسم 15 من وثيقة المنتج، وشرط <c>docs/api.md</c> صراحةً). العناوين المخطَّطة
/// <c>::ffff:a.b.c.d</c> تُطبَّع إلى IPv4 حتى لا يظهر العنوان الواحد مرتين.</para>
///
/// <para><b>ما لا يُحتسب:</b> اتصال اجتاز AUTH1 (ولو خسر السباق: <c>superseded</c>)، واتصال أُلغي بإلغائنا نحن عند
/// فوز اتصال آخر أو انتهاء نافذة الاتصال. الغرض إشارة أمنية بلا إيجابيات كاذبة، فالشك يُحسب لصالح الصمت.</para>
///
/// آمن للخيوط: حلقة القبول ومعالجات المصادقة تكتب فيه بالتوازي.
/// </summary>
public sealed class UnauthenticatedProbeLog
{
    /// <summary>سقف العناوين المميزة المحفوظة (نفس الحد في <c>docs/api.md</c>: <c>unauthenticated_peers</c> ≤ 10).</summary>
    public const int MaxPeers = 10;

    private readonly object _gate = new();
    private readonly List<string> _peers = new(MaxPeers);
    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
    private int _count;
    private int _distinctPeers;

    /// <summary>عدد الاتصالات الواردة التي أُغلقت قبل اجتياز AUTH1.</summary>
    public int Count => Volatile.Read(ref _count);

    /// <summary>عدد العناوين المميزة التي رُئيت فعلًا (قد يتجاوز <see cref="MaxPeers"/> بينما القائمة لا تتجاوزه).</summary>
    public int DistinctPeers => Volatile.Read(ref _distinctPeers);

    /// <summary>أول <see cref="MaxPeers"/> عنوانًا مميزًا بترتيب الظهور.</summary>
    public IReadOnlyList<string> Peers
    {
        get { lock (_gate) return _peers.ToArray(); }
    }

    /// <summary>يسجّل محاولة غير مصادَقة من <paramref name="remote"/> (null = عنوان غير معروف: يُعدّ ولا يُسجَّل).</summary>
    public void Record(IPAddress? remote)
    {
        Interlocked.Increment(ref _count);
        if (remote is null) return;
        var text = Normalize(remote);
        lock (_gate)
        {
            if (!_seen.Add(text)) return;
            _distinctPeers = _seen.Count;
            if (_peers.Count < MaxPeers) _peers.Add(text);
        }
    }

    /// <summary>الشكل الذي يُرسل في <c>data</c> عند <c>POST /api/v1/diagnostics</c>، أو null إن لم تقع أي محاولة.</summary>
    public IReadOnlyDictionary<string, object?>? ToDiagnostics(int listenerPort)
    {
        var count = Count;
        if (count <= 0) return null;
        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["listener_unauthenticated"] = count,
            ["listener_port"] = listenerPort,
            ["unauthenticated_peers"] = Peers.ToList(),
        };
    }

    /// <summary>‏<c>::ffff:a.b.c.d</c> ← <c>a.b.c.d</c>، ونطاق المدى (scope id) يُحذف من عناوين IPv6 المحلية.</summary>
    private static string Normalize(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (address.AddressFamily == AddressFamily.InterNetworkV6 && address.ScopeId != 0)
            address = new IPAddress(address.GetAddressBytes());
        return address.ToString();
    }
}
