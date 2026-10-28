using System.Net;
using Josour.Tunnel.Candidates;
using Josour.Tunnel.Tests.Mux;

namespace Josour.Tunnel.Tests;

/// <summary>Which on-link networks the host blocks (docs/protocol.md section 6 rule 6), and the mux's disposal after a failure.</summary>
public class LocalNetworkPolicyTests
{
    [Theory]
    // The host's own global IPv6 prefix: the one that matters, it holds the router, the NAS and the printer.
    [InlineData("2a02:1234:5678:9abc::42", 64, "2a02:1234:5678:9abc::/64")]
    [InlineData("2a02:1234:5678:9abc:1:2:3:4", 56, "2a02:1234:5678:9a00::/56")]
    [InlineData("2a02:1234:5678:9abc::42", 128, "2a02:1234:5678:9abc::42/128")]
    // IPv4: only subnets narrower than /16, public or private.
    [InlineData("198.51.99.7", 24, "198.51.99.0/24")]
    [InlineData("192.168.1.20", 24, "192.168.1.0/24")]
    [InlineData("100.70.1.2", 20, "100.70.0.0/20")]
    [InlineData("::ffff:198.51.99.7", 24, "198.51.99.0/24")]
    public void PolicyNetworkFor_Blocks(string address, int prefix, string expected)
    {
        var network = LocalNetwork.PolicyNetworkFor(IPAddress.Parse(address), prefix);
        Assert.NotNull(network);
        Assert.Equal(IPNetwork.Parse(expected), network!.Value);
    }

    [Theory]
    [InlineData("2a02:1234:5678:9abc::42", 32)]     // too wide: an ISP's allocation, not a link
    [InlineData("2a02:1234:5678:9abc::42", 0)]      // a platform that reports nothing useful
    [InlineData("fe80::1", 64)]                     // link-local: already a blocked range
    [InlineData("fd00::1", 64)]                     // ULA: likewise
    [InlineData("2002:c633:6307::1", 48)]           // 6to4: unwrapped instead
    [InlineData("2001:0:4136:e378::1", 64)]         // Teredo: likewise
    [InlineData("::1", 128)]
    [InlineData("203.0.113.9", 32)]                 // the exact address: covered by the address list, never widened
    [InlineData("130.10.20.30", 16)]                // a /16 or wider may be a whole organisation's block of public sites
    [InlineData("10.1.2.3", 8)]
    public void PolicyNetworkFor_Skips(string address, int prefix)
    {
        Assert.Null(LocalNetwork.PolicyNetworkFor(IPAddress.Parse(address), prefix));
    }

    [Fact]
    public void GetPolicyLocalNetworks_NeverThrows_AndNeverBlocksOrdinaryPublicSites()
    {
        var networks = LocalNetwork.GetPolicyLocalNetworks();
        foreach (var network in networks)
        {
            Assert.True(network.PrefixLength >= (network.BaseAddress.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
                ? LocalNetwork.MinPolicyPrefixV4
                : LocalNetwork.MinPolicyPrefixV6), network.ToString());
        }
    }

    [Fact]
    public async Task MuxDispose_AfterTheTransportDied_Completes()
    {
        var pair = await MuxPair.CreateAsync(blackholeGuest: true);
        pair.GuestRaw!.Dispose();
        await Assert.ThrowsAsync<Josour.Tunnel.Mux.MuxClosedException>(() => pair.Host.Completion.WaitAsync(TimeSpan.FromSeconds(15)));

        // The failure closed the mux without the orderly shutdown; disposing still waits for the loops and does not throw, twice.
        await pair.Host.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        await pair.Host.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        await pair.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(20));
    }
}
