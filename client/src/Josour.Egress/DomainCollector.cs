using System.Collections.Concurrent;

namespace Josour.Egress;

/// <summary>The set of distinct (normalised) domains opened through the tunnel, for session.end.domains.</summary>
public sealed class DomainCollector
{
    private readonly ConcurrentDictionary<string, byte> _domains = new(StringComparer.Ordinal);

    public int Count => _domains.Count;

    /// <summary>Adds a normalised name (lowercase, Punycode, no trailing dot). It returns true if it was new.</summary>
    public bool Add(string normalizedHost)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(normalizedHost);
        return _domains.TryAdd(normalizedHost, 0);
    }

    public bool Contains(string normalizedHost) => _domains.ContainsKey(normalizedHost);

    public IReadOnlyCollection<string> Snapshot() => _domains.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList();
}
