using System.Net;
using Josour.Core.Allowlist;
using Josour.Core.Net;

namespace Josour.Core.Tests;

/// <summary>
/// The second round of hardening rule 6 (docs/protocol.md section 6): the ranges and wrappings that were missing
/// (SIIT, the RFC 8215 local-use NAT64 prefix, site-local, discard, documentation, the 6to4 relay anycast),
/// and the host's on-link networks, which keep the guest away from the host's LAN neighbours' global IPv6 addresses.
/// </summary>
public class IpRangePolicyNetworkTests
{
    [Theory]
    [InlineData("192.88.99.1")]                         // the deprecated 6to4 relay anycast (RFC 7526)
    [InlineData("100::1")]                              // discard (RFC 6666)
    [InlineData("2001:db8::1")]                         // documentation
    [InlineData("fec0::1")]                             // the deprecated site-local range, still routed on some internal networks
    [InlineData("fed0:1234::1")]
    public void NewRanges_AreBlocked(string ip)
    {
        Assert.True(IpRangePolicy.IsBlockedRange(IPAddress.Parse(ip)), ip);
        Assert.True(IpRangePolicy.IsBlocked(IPAddress.Parse(ip)), ip);
    }

    [Theory]
    // SIIT, RFC 6145/7915: ::ffff:0:a.b.c.d
    [InlineData("::ffff:0:10.0.0.1", true)]
    [InlineData("::ffff:0:127.0.0.1", true)]
    [InlineData("::ffff:0:169.254.169.254", true)]
    [InlineData("::ffff:0:8.8.8.8", false)]
    public void Siit_IsUnwrapped(string ip, bool blocked)
    {
        Assert.Equal(blocked, IpRangePolicy.IsBlocked(IPAddress.Parse(ip)));
        Assert.Equal(IPAddress.Parse(ip.Split(':').Last()), IpRangePolicy.ExtractEmbeddedIPv4(IPAddress.Parse(ip)));
    }

    [Theory]
    [InlineData("10.0.0.1")]
    [InlineData("127.0.0.1")]
    [InlineData("192.168.1.1")]
    [InlineData("169.254.169.254")]
    public void Nat64LocalUse_PrivateTargets_AreBlockedAtEveryRfc6052Position(string v4)
    {
        foreach (var bits in new[] { 96, 64, 56, 48 })
        {
            var address = LocalUseNat64(v4, bits);
            Assert.True(IpRangePolicy.IsBlocked(address), $"{address} (/{bits} encoding of {v4})");
        }
    }

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("1.1.1.1")]
    [InlineData("93.184.216.34")]
    public void Nat64LocalUse_PublicTargets_StayReachable_AtEveryPosition(string v4)
    {
        // The important direction: an IPv6-only host behind a local-use translator must still reach ordinary IPv4 sites.
        foreach (var bits in new[] { 96, 64, 56, 48 })
        {
            var address = LocalUseNat64(v4, bits);
            Assert.False(IpRangePolicy.IsBlocked(address), $"{address} (/{bits} encoding of {v4})");
        }
    }

    [Fact]
    public void Nat64LocalUse_AnOperatorPrefixInsideThe48_DoesNotReadAsAPrivateAddress()
    {
        // 64:ff9b:1:a00::/96 — the operator's bytes 0a00 would read as 10.0.x.x at /48 were the encoding's validity not checked.
        var address = IPAddress.Parse("64:ff9b:1:a00::808:808");
        Assert.False(IpRangePolicy.IsBlocked(address));
        Assert.True(IpRangePolicy.IsBlocked(IPAddress.Parse("64:ff9b:1:a00::a00:1")));
    }

    [Fact]
    public void OnLinkV6Prefix_BlocksTheHostsNeighbours_NotTheRestOfTheInternet()
    {
        var networks = new[] { new IPNetwork(IPAddress.Parse("2a02:1234:5678:9abc::"), 64) };
        Assert.True(IpRangePolicy.IsBlocked(IPAddress.Parse("2a02:1234:5678:9abc::1"), null, networks));          // the router
        Assert.True(IpRangePolicy.IsBlocked(IPAddress.Parse("2a02:1234:5678:9abc:aaaa:bbbb:cccc:dddd"), null, networks));
        Assert.False(IpRangePolicy.IsBlocked(IPAddress.Parse("2a02:1234:5678:9abd::1"), null, networks));        // the next /64
        Assert.False(IpRangePolicy.IsBlocked(IPAddress.Parse("2606:4700::1111"), null, networks));
        Assert.False(IpRangePolicy.IsBlocked(IPAddress.Parse("8.8.8.8"), null, networks));
    }

    [Fact]
    public void OnLinkV4Subnet_IsCheckedThroughEveryWrapper()
    {
        // An internal network numbered from public space.
        var networks = new[] { new IPNetwork(IPAddress.Parse("198.51.99.0"), 24) };
        foreach (var form in new[] { "198.51.99.7", "::ffff:198.51.99.7", "64:ff9b::198.51.99.7", "2002:c633:6307::1", "::ffff:0:198.51.99.7" })
            Assert.True(IpRangePolicy.IsBlocked(IPAddress.Parse(form), Array.Empty<IPAddress>(), networks), form);
        Assert.False(IpRangePolicy.IsBlocked(IPAddress.Parse("198.51.98.7"), Array.Empty<IPAddress>(), networks));
    }

    [Fact]
    public void NoNetworks_IsThePreviousBehaviour()
    {
        var neighbour = IPAddress.Parse("2a02:1234:5678:9abc::1");
        Assert.False(IpRangePolicy.IsBlocked(neighbour, Array.Empty<IPAddress>(), null));
        Assert.False(IpRangePolicy.IsBlocked(neighbour, Array.Empty<IPAddress>(), Array.Empty<IPNetwork>()));
        Assert.False(IpRangePolicy.IsBlocked(neighbour));
    }

    [Fact]
    public void DenyAllAllowlist_AllowsNothing()
    {
        var list = DenyAllAllowlist.Instance;
        Assert.False(list.IsAllowed("example.com", 443, new[] { 80, 443 }));
        Assert.False(list.HostMatches("example.com"));
        Assert.Empty(list.Entries);
    }

    /// <summary>The RFC 6052 encoding of <paramref name="v4"/> under 64:ff9b:1::/48 for a translator prefix of <paramref name="bits"/>.</summary>
    private static IPAddress LocalUseNat64(string v4, int bits)
    {
        var b = IPAddress.Parse("64:ff9b:1::").GetAddressBytes();
        var a = IPAddress.Parse(v4).GetAddressBytes();
        switch (bits)
        {
            case 96: a.CopyTo(b, 12); break;
            case 64: b[9] = a[0]; b[10] = a[1]; b[11] = a[2]; b[12] = a[3]; break;
            case 56: b[7] = a[0]; b[9] = a[1]; b[10] = a[2]; b[11] = a[3]; break;
            case 48: b[6] = a[0]; b[7] = a[1]; b[9] = a[2]; b[10] = a[3]; break;
            default: throw new ArgumentOutOfRangeException(nameof(bits));
        }
        return new IPAddress(b);
    }
}
