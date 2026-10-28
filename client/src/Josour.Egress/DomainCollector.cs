using System.Collections.Concurrent;

namespace Josour.Egress;

/// <summary>The set of distinct (normalised) domains opened through the tunnel, for session.end.domains.</summary>
public sealed class DomainCollector
{
    /// <summary>
    /// The most names one session reports: the server reads no more than this (<c>MAX_REPORTED_DOMAINS</c> in
    /// backend/app/ws/protocol.py), and an unbounded list would push session.end past the control channel's frame limit.
    /// </summary>
    public const int MaxDomains = 2000;

    private readonly ConcurrentDictionary<string, byte> _domains = new(StringComparer.Ordinal);

    public int Count => _domains.Count;

    /// <summary>
    /// Adds a normalised name (lowercase, Punycode, no trailing dot). It returns true if it was new; once
    /// <see cref="MaxDomains"/> names are held, further names are not recorded.
    /// </summary>
    public bool Add(string normalizedHost)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(normalizedHost);
        if (_domains.Count >= MaxDomains) return false;
        return _domains.TryAdd(normalizedHost, 0);
    }

    public bool Contains(string normalizedHost) => _domains.ContainsKey(normalizedHost);

    // Take: under concurrency a few Adds can pass the Count check together.
    public IReadOnlyCollection<string> Snapshot() => _domains.Keys.OrderBy(k => k, StringComparer.Ordinal).Take(MaxDomains).ToList();
}
