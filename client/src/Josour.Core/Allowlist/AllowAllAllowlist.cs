namespace Josour.Core.Allowlist;

/// <summary>
/// كل اسم مسموح، والمنفذ وحده يقيّد ([ADR-0010](../../../../docs/decisions/0010-route-all-through-host.md)).
///
/// <para>
/// هذا هو الوضع الافتراضي بعد ADR-0010: غرض المنتج فتح المواقع التي لا تعمل إلا من دولة المضيف، وقائمة
/// يديرها المسؤول يدويًا تناقض ذلك عمليًا — صفحة واحدة تسحب مواردها من عشرات النطاقات الفرعية، فيصير على
/// المستخدم اكتشاف كل واحد منها. القائمة تبقى موجودة كضابط اختياري حين يريد المسؤول تقييد وجهات جلسة بعينها.
/// </para>
///
/// <para><b>ما لا يغيّره هذا الصنف — وهو أهم ما فيه:</b> رفع الشرط الثالث فقط من خطوات سياسة الخروج الثماني
/// (<c>docs/protocol.md</c> القسم 6). تبقى نافذة: رفض العناوين الحرفية، والتطبيع الصارم، وحد
/// <c>allowed_ports</c>، وحل DNS مرة واحدة، و<b>رفض أي عنوان ناتج محظور</b> — وهو الحارس الذي يمنع الوصول
/// إلى شبكة المضيف المحلية وصفحة راوتره و<c>localhost</c> وعنوانه العام. «مرّر كل المواقع» لا تعني ولن تعني
/// تعطيل ذلك الحارس، ومعيار القبول 15 يبقى أخضر.</para>
/// </summary>
public sealed class AllowAllAllowlist : IAllowlist
{
    /// <param name="version">إصدار القائمة التي كانت ستُطبَّق لو كانت مفعّلة؛ يُنقل كما هو في التشخيص والإفصاح.</param>
    public AllowAllAllowlist(int version = 0) => Version = version;

    public int Version { get; }

    /// <summary>فارغة عمدًا: لا مدخلات تُعرض ولا تُطابَق، فالسماح غير مشروط بالاسم.</summary>
    public IReadOnlyList<AllowlistEntry> Entries { get; } = Array.Empty<AllowlistEntry>();

    /// <summary>المنفذ وحده يقيّد؛ <c>allowed_ports</c> تبقى 80 و443.</summary>
    public bool IsAllowed(string normalizedHost, int port, IReadOnlyList<int> allowedPorts)
    {
        ArgumentNullException.ThrowIfNull(allowedPorts);
        return !string.IsNullOrEmpty(normalizedHost) && allowedPorts.Contains(port);
    }

    /// <summary>كل اسم يطابق: وهذا ما يجعل <c>http://</c> يُرد بـ 307 إلى https بدل أن يسلك المسار المباشر.</summary>
    public bool HostMatches(string normalizedHost) => !string.IsNullOrEmpty(normalizedHost);
}
