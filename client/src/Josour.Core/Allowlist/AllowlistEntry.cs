namespace Josour.Core.Allowlist;

/// <summary>مدخل قائمة المواقع بعد التطبيع. الصيغ: example.com | =exact.com | host:port | =host:port</summary>
public sealed record AllowlistEntry(string Host, bool ExactOnly, int? Port);

public interface IAllowlist
{
    int Version { get; }
    IReadOnlyList<AllowlistEntry> Entries { get; }
    /// <summary>هل يُسمح بالمرور عبر المضيف لهذا الاسم والمنفذ؟ الاسم مطبَّع (أحرف صغيرة، Punycode، بلا نقطة أخيرة).</summary>
    bool IsAllowed(string normalizedHost, int port, IReadOnlyList<int> allowedPorts);
}
