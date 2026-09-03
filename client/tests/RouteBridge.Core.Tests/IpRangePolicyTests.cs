using System.Net;
using RouteBridge.Core.Net;

namespace RouteBridge.Core.Tests;

public class IpRangePolicyTests
{
    [Theory]
    // IPv4 ranges from docs/protocol.md §6 rule 6 (first and last address of each range)
    [InlineData("0.0.0.0")] [InlineData("0.255.255.255")]
    [InlineData("10.0.0.0")] [InlineData("10.255.255.255")]
    [InlineData("100.64.0.0")] [InlineData("100.127.255.255")]
    [InlineData("127.0.0.1")] [InlineData("127.255.255.255")]
    [InlineData("169.254.0.0")] [InlineData("169.254.255.255")]
    [InlineData("172.16.0.0")] [InlineData("172.31.255.255")]
    [InlineData("192.0.0.0")] [InlineData("192.0.0.255")]
    [InlineData("192.0.2.0")] [InlineData("192.0.2.255")]
    [InlineData("192.168.0.0")] [InlineData("192.168.255.255")]
    [InlineData("198.18.0.0")] [InlineData("198.19.255.255")]
    [InlineData("198.51.100.0")] [InlineData("198.51.100.255")]
    [InlineData("203.0.113.0")] [InlineData("203.0.113.255")]
    [InlineData("224.0.0.0")] [InlineData("239.255.255.255")]
    [InlineData("240.0.0.0")] [InlineData("255.255.255.254")]
    [InlineData("255.255.255.255")]
    // IPv6 ranges
    [InlineData("::")] [InlineData("::1")]
    [InlineData("fc00::")] [InlineData("fdff:ffff:ffff:ffff:ffff:ffff:ffff:ffff")]
    [InlineData("fe80::")] [InlineData("febf:ffff:ffff:ffff:ffff:ffff:ffff:ffff")]
    [InlineData("ff00::")] [InlineData("ff02::1")] [InlineData("ffff:ffff:ffff:ffff:ffff:ffff:ffff:ffff")]
    // IPv4-mapped ::ffff:0:0/96 (embedded v4 blocked)
    [InlineData("::ffff:10.0.0.1")] [InlineData("::ffff:127.0.0.1")] [InlineData("::ffff:192.168.1.1")] [InlineData("::ffff:169.254.1.1")]
    // NAT64 64:ff9b::/96 (embedded v4 blocked)
    [InlineData("64:ff9b::10.0.0.1")] [InlineData("64:ff9b::c0a8:101")] [InlineData("64:ff9b::7f00:1")]
    // 6to4 2002::/16 (embedded v4 in bits 16-47 blocked)
    [InlineData("2002:0a00:0001::")] [InlineData("2002:c0a8:0101::1")] [InlineData("2002:7f00:0001::")] [InlineData("2002:a9fe:0101::")]
    // Teredo 2001::/32 (last 32 bits XOR 0xFFFFFFFF = client v4; 3f57:fefe -> 192.168.1.1, f5ff:fffe -> 10.0.0.1)
    [InlineData("2001:0:4136:e378:8000:63bf:3f57:fefe")] [InlineData("2001::f5ff:fffe")] [InlineData("2001:0:0:0:0:0:80ff:fffe")]
    public void BlockedRanges_AreBlocked(string ip)
    {
        Assert.True(IpRangePolicy.IsBlocked(IPAddress.Parse(ip)), ip);
        Assert.True(IpRangePolicy.IsBlockedRange(IPAddress.Parse(ip)), ip);
    }

    [Theory]
    [InlineData("1.0.0.0")] [InlineData("8.8.8.8")] [InlineData("1.1.1.1")] [InlineData("9.255.255.255")] [InlineData("11.0.0.0")]
    [InlineData("100.63.255.255")] [InlineData("100.128.0.0")]
    [InlineData("126.255.255.255")] [InlineData("128.0.0.1")]
    [InlineData("169.253.255.255")] [InlineData("169.255.0.0")]
    [InlineData("172.15.255.255")] [InlineData("172.32.0.0")]
    [InlineData("192.0.1.0")] [InlineData("192.0.3.0")]
    [InlineData("192.167.255.255")] [InlineData("192.169.0.0")]
    [InlineData("198.17.255.255")] [InlineData("198.20.0.0")]
    [InlineData("198.51.99.255")] [InlineData("198.51.101.0")]
    [InlineData("203.0.112.255")] [InlineData("203.0.114.0")]
    [InlineData("223.255.255.255")]
    [InlineData("2001:4860:4860::8888")] [InlineData("2606:4700::1111")] [InlineData("2a00:1450:4001::1")]
    [InlineData("2001:db8::1")]
    [InlineData("::ffff:8.8.8.8")]
    [InlineData("64:ff9b::8.8.8.8")]
    [InlineData("2002:0808:0808::1")]
    [InlineData("2001:0:4136:e378:8000:63bf:f7f7:f7f7")]
    [InlineData("fbff:ffff:ffff:ffff:ffff:ffff:ffff:ffff")] [InlineData("fe00::1")] [InlineData("fec0::1")]
    public void PublicAddresses_AreNotBlocked(string ip)
    {
        Assert.False(IpRangePolicy.IsBlocked(IPAddress.Parse(ip)), ip);
        Assert.False(IpRangePolicy.IsBlocked(IPAddress.Parse(ip), Array.Empty<IPAddress>()), ip);
    }

    [Fact]
    public void LocalAddresses_AreBlocked_IncludingWrappedForms()
    {
        var locals = new[] { IPAddress.Parse("8.8.4.4"), IPAddress.Parse("2606:4700::1111") };
        Assert.True(IpRangePolicy.IsBlocked(IPAddress.Parse("8.8.4.4"), locals));
        Assert.True(IpRangePolicy.IsBlocked(IPAddress.Parse("::ffff:8.8.4.4"), locals));
        Assert.True(IpRangePolicy.IsBlocked(IPAddress.Parse("64:ff9b::8.8.4.4"), locals));
        Assert.True(IpRangePolicy.IsBlocked(IPAddress.Parse("2002:0808:0404::"), locals));
        Assert.True(IpRangePolicy.IsBlocked(IPAddress.Parse("2606:4700::1111"), locals));
        Assert.False(IpRangePolicy.IsBlocked(IPAddress.Parse("8.8.8.8"), locals));
        Assert.False(IpRangePolicy.IsBlocked(IPAddress.Parse("2606:4700::1112"), locals));
    }

    [Fact]
    public void LocalAddresses_GivenAsMapped_StillBlockPlainV4()
    {
        var locals = new[] { IPAddress.Parse("::ffff:8.8.4.4") };
        Assert.True(IpRangePolicy.IsBlocked(IPAddress.Parse("8.8.4.4"), locals));
    }

    [Theory]
    [InlineData("::ffff:1.2.3.4", "1.2.3.4")]
    [InlineData("64:ff9b::8.8.8.8", "8.8.8.8")]
    [InlineData("2002:0808:0808::1", "8.8.8.8")]
    [InlineData("2002:c0a8:0101::", "192.168.1.1")]
    [InlineData("2001:0:4136:e378:8000:63bf:3f57:fefe", "192.168.1.1")]
    [InlineData("2001:4860:4860::8888", null)]
    [InlineData("2001:db8::1", null)]
    [InlineData("fe80::1", null)]
    [InlineData("1.2.3.4", null)]
    public void ExtractEmbeddedIPv4_Unwraps(string ip, string? expected)
    {
        var embedded = IpRangePolicy.ExtractEmbeddedIPv4(IPAddress.Parse(ip));
        Assert.Equal(expected is null ? null : IPAddress.Parse(expected), embedded);
    }

    [Theory]
    [InlineData("1.2.3.4", true)]
    [InlineData("[::1]", true)]
    [InlineData("::1", true)]
    [InlineData("2001:db8::1", true)]
    [InlineData("[2001:db8::1]", true)]
    [InlineData("fe80::1%eth0", true)]
    [InlineData("::ffff:1.2.3.4", true)]
    [InlineData("example.com", false)]
    [InlineData("localhost", false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData(null, false)]
    [InlineData("xn--bcher-kva.de", false)]
    [InlineData("[example.com]", false)]
    [InlineData("1e100.net", false)]
    public void IsIpLiteral_DetectsAddresses(string? host, bool expected)
    {
        Assert.Equal(expected, IpRangePolicy.IsIpLiteral(host));
    }
}
