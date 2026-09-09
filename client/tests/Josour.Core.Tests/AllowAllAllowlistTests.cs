using Josour.Core.Allowlist;

namespace Josour.Core.Tests;

/// <summary>
/// ADR-0010's default: every site the work browser asks for goes through the host.
///
/// <para>What these mostly pin is the boundary that did <b>not</b> move. "Route everything" lifts step
/// three of the egress policy - the name match - and nothing else. The port cap still applies, and the
/// private-address guard in step six is untouched by construction, because this type cannot express an
/// opinion about addresses at all: it answers questions about names, and the address check runs after
/// DNS on both sides regardless of what any allow-list said.</para>
/// </summary>
public class AllowAllAllowlistTests
{
    private static readonly int[] Ports = { 80, 443 };

    [Theory]
    [InlineData("example.com")]
    [InlineData("a.very.deep.subdomain.example.co.uk")]
    [InlineData("xn--mgbh0fb.example")] // punycode, as the normaliser produces
    public void EveryNameIsAllowedOnAnAllowedPort(string host)
    {
        var allowlist = new AllowAllAllowlist();
        Assert.True(allowlist.IsAllowed(host, 443, Ports));
        Assert.True(allowlist.IsAllowed(host, 80, Ports));
        Assert.True(allowlist.HostMatches(host));
    }

    [Theory]
    [InlineData(22)]
    [InlineData(3389)]
    [InlineData(8080)]
    public void ThePortCapStillApplies(int port)
    {
        // "any site" was never "any port": allowed_ports stays 80/443, so the tunnel does not become a
        // way to reach arbitrary services on whatever the host can route to.
        Assert.False(new AllowAllAllowlist().IsAllowed("example.com", port, Ports));
    }

    [Fact]
    public void AnEmptyNameIsStillNotAName()
    {
        var allowlist = new AllowAllAllowlist();
        Assert.False(allowlist.IsAllowed(string.Empty, 443, Ports));
        Assert.False(allowlist.HostMatches(string.Empty));
    }

    [Fact]
    public void ItCarriesTheVersionItWouldHaveEnforced()
    {
        // The version still travels: diagnostics and the host's disclosure both refer to it even when
        // nothing is being restricted.
        Assert.Equal(9, new AllowAllAllowlist(9).Version);
        Assert.Empty(new AllowAllAllowlist(9).Entries);
    }

    [Fact]
    public void HostMatchesIsTrueSoHttpIsRedirectedRatherThanSentInTheClear()
    {
        // The proxy answers http:// with a local 307 to https for anything the list matches, and takes
        // the direct path for anything it does not. With no restriction, everything must take the first
        // branch - otherwise "route everything through the host" would quietly except plain HTTP.
        Assert.True(new AllowAllAllowlist().HostMatches("example.com"));
    }
}
