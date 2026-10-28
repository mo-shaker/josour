using System.Net;
using Josour.Core.Allowlist;
using Josour.Core.Net;
using Josour.Tunnel;
using Josour.Tunnel.Mux;

namespace Josour.Egress.Tests;

/// <summary>
/// Rule 1 after normalisation, and the host's on-link networks in rule 6 (docs/protocol.md section 6).
/// </summary>
public class EgressPolicyNormalisationTests
{
    [Theory]
    [InlineData("8.8.8.8.")]                 // the trailing dot: not a literal to IPAddress.TryParse, a literal once stripped
    [InlineData("127.0.0.1.")]
    [InlineData("10.0.0.1..")]
    [InlineData("0x7f.0.0.1.")]
    [InlineData("１２７.０.０.１")]            // fullwidth digits, which IDN maps to ASCII
    [InlineData("８.８.８.８")]
    public void ANameThatNormalisesToALiteral_IsAnIpLiteral(string host)
    {
        var resolver = new StubResolver();
        var policy = new EgressPolicy(new AllowAllAllowlist(), new EgressPolicyOptions { Resolver = resolver });
        Assert.Equal(OpenFailReason.IpLiteral, policy.Evaluate(host, 443, out _));
    }

    [Fact]
    public async Task ANameThatNormalisesToALiteral_IsNeverResolved()
    {
        var resolver = new StubResolver().Map("8.8.8.8", "8.8.8.8");
        var policy = new EgressPolicy(new AllowAllAllowlist(), new EgressPolicyOptions { Resolver = resolver });
        var result = await policy.OpenAsync("8.8.8.8.", 443, CancellationToken.None);
        Assert.Equal(OpenFailReason.IpLiteral, result.Reason);
        Assert.Equal(0, resolver.Calls);
    }

    [Fact]
    public async Task ANameResolvingIntoTheHostsOnLinkV6Prefix_IsRefused()
    {
        // The host's LAN neighbours have global addresses in the host's own /64: its router, a NAS, a printer.
        var resolver = new StubResolver().Map("nas.evil.example", "2a02:1234:5678:9abc::1");
        var policy = new EgressPolicy(new AllowAllAllowlist(), new EgressPolicyOptions
        {
            Resolver = resolver,
            LocalAddresses = new[] { IPAddress.Parse("2a02:1234:5678:9abc::42") },
            LocalNetworks = new[] { new IPNetwork(IPAddress.Parse("2a02:1234:5678:9abc::"), 64) },
        });

        // Refused after resolution and before any socket: the address is a neighbour, not the host itself.
        var refused = await policy.OpenAsync("nas.evil.example", 443, CancellationToken.None);
        Assert.Equal(OpenFailReason.PrivateIp, refused.Reason);
        Assert.Equal("nas.evil.example", refused.NormalizedHost);
    }

    [Fact]
    public async Task TheAdapter_PassesTheContextsNetworksToThePolicy()
    {
        var resolver = new StubResolver().Map("nas.evil.example", "2a02:1234:5678:9abc::1");
        var acceptor = new FakeAcceptor();
        var context = new TunnelEgressContext(
            new AllowAllAllowlist(), new[] { 80, 443 }, resolver, Array.Empty<IPAddress>(), acceptor,
            BlockedLocalNetworks: new[] { new IPNetwork(IPAddress.Parse("2a02:1234:5678:9abc::"), 64) });
        await using var egress = EgressTunnelAdapter.Create(context);
        egress.Attach(acceptor);

        var decision = await acceptor.OpenRequested!(new MuxOpenRequest("nas.evil.example", 443), CancellationToken.None);
        Assert.Equal(OpenFailReason.PrivateIp, decision.Reason);
    }
}
