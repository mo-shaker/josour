namespace Josour.Core.Allowlist;

/// <summary>
/// Every name is allowed, and the port alone restricts ([ADR-0010](../../../../docs/decisions/0010-route-all-through-host.md)).
///
/// <para>
/// This is the default since ADR-0010: the product exists to open sites that only work from the host's country,
/// and a list an administrator maintains by hand contradicts that in practice — a single page pulls its resources
/// from dozens of subdomains, so the user would have to discover every one of them. The list stays as an optional
/// control for when an administrator wants to restrict a particular session's destinations.
/// </para>
///
/// <para><b>What this class does not change — and it is the most important thing about it:</b> it lifts the third
/// condition only of the eight egress-policy steps (<c>docs/protocol.md</c> section 6). Still in force: refusing
/// address literals, strict normalisation, the <c>allowed_ports</c> limit, resolving DNS once, and <b>refusing any
/// resulting blocked address</b> — the guard that keeps the guest away from the host's local network, their router's
/// page, <c>localhost</c> and their public address. "Pass every site" does not and will not mean disabling that
/// guard, and acceptance criterion 15 stays green.</para>
/// </summary>
public sealed class AllowAllAllowlist : IAllowlist
{
    /// <param name="version">The version of the list that would have applied had it been enforced; carried through unchanged into the diagnostics and the disclosure.</param>
    public AllowAllAllowlist(int version = 0) => Version = version;

    public int Version { get; }

    /// <summary>Deliberately empty: no entries are shown and none are matched, so the permission is not conditional on the name.</summary>
    public IReadOnlyList<AllowlistEntry> Entries { get; } = Array.Empty<AllowlistEntry>();

    /// <summary>The port alone restricts; <c>allowed_ports</c> stays 80 and 443.</summary>
    public bool IsAllowed(string normalizedHost, int port, IReadOnlyList<int> allowedPorts)
    {
        ArgumentNullException.ThrowIfNull(allowedPorts);
        return !string.IsNullOrEmpty(normalizedHost) && allowedPorts.Contains(port);
    }

    /// <summary>Every name matches: which is what makes <c>http://</c> answered with a 307 to https instead of taking the direct path.</summary>
    public bool HostMatches(string normalizedHost) => !string.IsNullOrEmpty(normalizedHost);
}
