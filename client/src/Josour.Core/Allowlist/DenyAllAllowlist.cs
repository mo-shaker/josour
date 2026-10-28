namespace Josour.Core.Allowlist;

/// <summary>
/// No list was supplied: no name is allowed, and — because it is not an enforced <see cref="AllowlistMatcher"/> — the guest's proxy
/// has no direct path either. The fail-closed default for whoever builds a session without saying which list applies, so a forgotten
/// setting can neither open the host to every site nor send the guest's traffic out of its own connection.
/// </summary>
public sealed class DenyAllAllowlist : IAllowlist
{
    public static DenyAllAllowlist Instance { get; } = new();

    private DenyAllAllowlist() { }

    public int Version => 0;

    public IReadOnlyList<AllowlistEntry> Entries { get; } = Array.Empty<AllowlistEntry>();

    public bool IsAllowed(string normalizedHost, int port, IReadOnlyList<int> allowedPorts) => false;

    public bool HostMatches(string normalizedHost) => false;
}
