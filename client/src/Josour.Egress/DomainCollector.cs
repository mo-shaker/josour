using System.Collections.Concurrent;

namespace Josour.Egress;

/// <summary>مجموعة النطاقات المميزة (المطبَّعة) التي فُتحت عبر النفق، لـ session.end.domains.</summary>
public sealed class DomainCollector
{
    private readonly ConcurrentDictionary<string, byte> _domains = new(StringComparer.Ordinal);

    public int Count => _domains.Count;

    /// <summary>يضيف اسمًا مطبَّعًا (أحرف صغيرة، Punycode، بلا نقطة أخيرة). يعيد true إن كان جديدًا.</summary>
    public bool Add(string normalizedHost)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(normalizedHost);
        return _domains.TryAdd(normalizedHost, 0);
    }

    public bool Contains(string normalizedHost) => _domains.ContainsKey(normalizedHost);

    public IReadOnlyCollection<string> Snapshot() => _domains.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList();
}
