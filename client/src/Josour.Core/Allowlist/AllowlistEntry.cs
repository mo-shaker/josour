namespace Josour.Core.Allowlist;

/// <summary>A site-list entry after normalisation. The forms: example.com | =exact.com | host:port | =host:port</summary>
public sealed record AllowlistEntry(string Host, bool ExactOnly, int? Port);

public interface IAllowlist
{
    int Version { get; }
    IReadOnlyList<AllowlistEntry> Entries { get; }
    /// <summary>May this name and port pass through the host? The name is normalised (lowercase, Punycode, no trailing dot).</summary>
    bool IsAllowed(string normalizedHost, int port, IReadOnlyList<int> allowedPorts);

    /// <summary>
    /// Does the name match the list regardless of the port? It distinguishes "an allowed name on the wrong port" from
    /// "a name that is not allowed" in <c>OPEN_FAIL</c>, and it decides the 307 answer to <c>http://</c>.
    /// <para>
    /// A member of the contract rather than <c>Entries.Any(...)</c> at every call site: an allow-everything list
    /// (<see cref="AllowAllAllowlist"/>) has no entries, so walking <c>Entries</c> gave it "no match" and sent http down the direct path.
    /// </para>
    /// </summary>
    bool HostMatches(string normalizedHost);
}
