namespace Josour.Core.Allowlist;

/// <summary>مدخل قائمة المواقع بعد التطبيع. الصيغ: example.com | =exact.com | host:port | =host:port</summary>
public sealed record AllowlistEntry(string Host, bool ExactOnly, int? Port);

public interface IAllowlist
{
    int Version { get; }
    IReadOnlyList<AllowlistEntry> Entries { get; }
    /// <summary>هل يُسمح بالمرور عبر المضيف لهذا الاسم والمنفذ؟ الاسم مطبَّع (أحرف صغيرة، Punycode، بلا نقطة أخيرة).</summary>
    bool IsAllowed(string normalizedHost, int port, IReadOnlyList<int> allowedPorts);

    /// <summary>
    /// هل يطابق الاسم القائمة بغض النظر عن المنفذ؟ يميّز «اسم مسموح بمنفذ خاطئ» عن «اسم غير مسموح» في
    /// <c>OPEN_FAIL</c>، ويقرر رد 307 على <c>http://</c>.
    /// <para>
    /// عضو في العقد لا <c>Entries.Any(...)</c> عند كل مستدعٍ: قائمة «اسمح بالكل» (<see cref="AllowAllAllowlist"/>)
    /// لا مدخلات لها، فالمشي على <c>Entries</c> كان يعطيها «لا يطابق» ويرسل http عبر المسار المباشر.
    /// </para>
    /// </summary>
    bool HostMatches(string normalizedHost);
}
