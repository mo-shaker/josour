using System.Net;
using Josour.Tunnel.Candidates;

namespace Josour.Tunnel.Tests;

/// <summary>
/// The NAT verdict. The case that matters most is the one the old check could not express: no gateway
/// answered, so nothing about CGNAT is known - and saying "false" there was saying "clear" about the
/// commonest way this product fails to connect directly.
/// </summary>
public sealed class NatAssessmentTests
{
    private static IPAddress Ip(string s) => IPAddress.Parse(s);
    private static IPAddress[] Local(params string[] addresses) => addresses.Select(Ip).ToArray();

    // ---------------------------------------------------------------- the regression this class exists for

    [Fact]
    public void PhoneTethering_NoGatewayAnswers_ReportsUnknown_NotClear()
    {
        // A tethered laptop: private address from the phone, no UPnP, and a carrier NAT above that
        // nothing here can see. The old check returned cgnat_suspected=false and looked reassuring.
        var nat = NatAssessment.Assess(
            Local("192.168.43.17"), serverSeenPublicIp: Ip("41.234.9.8"),
            gatewayFound: false, mappingOk: false, gatewayExternalIp: null);

        Assert.False(nat.Checked);
        Assert.Equal("no_gateway_answered", nat.Evidence);

        // The point: false here is not an answer, and Checked is what says so.
        Assert.False(nat.CgnatSuspected);
        Assert.False(nat.Checked);

        // And this much IS known even without a gateway - the server sees an address we do not hold.
        Assert.Equal(NatReachability.Blocked, nat.Reachability);
    }

    [Fact]
    public void NoGateway_AndNoPublicAddressToCompare_IsWhollyUnknown()
    {
        var nat = NatAssessment.Assess(
            Local("192.168.1.20"), serverSeenPublicIp: null,
            gatewayFound: false, mappingOk: false, gatewayExternalIp: null);

        Assert.False(nat.Checked);
        Assert.Equal(NatReachability.Unknown, nat.Reachability);
    }

    [Fact]
    public void GatewayFoundButSilentAboutItsAddress_IsAlsoNotAnAnswer()
    {
        var nat = NatAssessment.Assess(
            Local("192.168.1.20"), serverSeenPublicIp: Ip("41.234.9.8"),
            gatewayFound: true, mappingOk: false, gatewayExternalIp: null);

        Assert.False(nat.Checked);
        Assert.Equal("gateway_found_but_reported_no_external_address", nat.Evidence);
    }

    // ------------------------------------------------------- CGNAT that needs no gateway to be visible

    [Theory]
    [InlineData("100.64.0.1")]
    [InlineData("100.71.14.9")]
    [InlineData("100.127.255.254")]
    public void ALocalAddressInSharedAddressSpace_IsCgnatOnItsOwn(string local)
    {
        var nat = NatAssessment.Assess(
            Local(local), serverSeenPublicIp: Ip("41.234.9.8"),
            gatewayFound: false, mappingOk: false, gatewayExternalIp: null);

        Assert.True(nat.CgnatSuspected);
        Assert.True(nat.Checked, "the address itself is the evidence - no gateway required");
        Assert.Equal(NatReachability.Blocked, nat.Reachability);
        Assert.Equal("local_address_in_100.64.0.0/10", nat.Evidence);
    }

    [Theory]
    [InlineData("100.63.255.255")]  // just below the range
    [InlineData("100.128.0.0")]     // just above it
    [InlineData("10.100.64.1")]     // the octets, in the wrong place
    public void AddressesOutsideSharedAddressSpaceAreNotCgnat(string local)
    {
        var nat = NatAssessment.Assess(
            Local(local), serverSeenPublicIp: Ip("41.234.9.8"),
            gatewayFound: false, mappingOk: false, gatewayExternalIp: null);

        Assert.False(nat.CgnatSuspected);
        Assert.NotEqual("local_address_in_100.64.0.0/10", nat.Evidence);
    }

    [Fact]
    public void AnIspRouterHandingOut100_64_WhileOwningThePublicAddress_IsOneNat_NotTwo()
    {
        // Real and rare: the LAN side uses shared address space, but the gateway's own outside address
        // is the address the server sees. One NAT, and it answers - so the port can be opened.
        var nat = NatAssessment.Assess(
            Local("100.75.0.4"), serverSeenPublicIp: Ip("41.234.9.8"),
            gatewayFound: true, mappingOk: true, gatewayExternalIp: Ip("41.234.9.8"));

        Assert.False(nat.CgnatSuspected);
        Assert.Equal(NatReachability.Mapped, nat.Reachability);
    }

    // ------------------------------------------------------------------ what the gateway can still tell us

    [Fact]
    public void AGatewayWhoseOutsideAddressIsPrivate_IsDoubleNat()
    {
        var nat = NatAssessment.Assess(
            Local("192.168.1.20"), serverSeenPublicIp: Ip("41.234.9.8"),
            gatewayFound: true, mappingOk: true, gatewayExternalIp: Ip("10.30.0.7"));

        Assert.True(nat.CgnatSuspected);
        Assert.True(nat.Checked);
        Assert.Equal(NatReachability.Blocked, nat.Reachability);
        Assert.Equal("gateway_external_address_is_private", nat.Evidence);
    }

    [Fact]
    public void AGatewayWhoseOutsideAddressIsNotWhatTheServerSees_HasSomethingAboveIt()
    {
        var nat = NatAssessment.Assess(
            Local("192.168.1.20"), serverSeenPublicIp: Ip("41.234.9.8"),
            gatewayFound: true, mappingOk: true, gatewayExternalIp: Ip("197.50.1.1"));

        Assert.True(nat.CgnatSuspected);
        Assert.Equal("gateway_external_address_differs_from_public", nat.Evidence);
    }

    [Fact]
    public void OneGatewayThatMapped_IsTheCaseDirectWasBuiltFor()
    {
        var nat = NatAssessment.Assess(
            Local("192.168.1.20"), serverSeenPublicIp: Ip("41.234.9.8"),
            gatewayFound: true, mappingOk: true, gatewayExternalIp: Ip("41.234.9.8"));

        Assert.False(nat.CgnatSuspected);
        Assert.True(nat.Checked);
        Assert.Equal(NatReachability.Mapped, nat.Reachability);
    }

    [Fact]
    public void OneGatewayThatRefusedTheMapping_IsCheckedAndStillBlocked()
    {
        var nat = NatAssessment.Assess(
            Local("192.168.1.20"), serverSeenPublicIp: Ip("41.234.9.8"),
            gatewayFound: true, mappingOk: false, gatewayExternalIp: Ip("41.234.9.8"));

        Assert.False(nat.CgnatSuspected, "one NAT that answers is not CGNAT, whatever it did with the mapping");
        Assert.True(nat.Checked);
        Assert.Equal(NatReachability.Blocked, nat.Reachability);
        Assert.Equal("single_nat_but_mapping_failed", nat.Evidence);
    }

    // ------------------------------------------------------------------------------ no NAT at all

    [Fact]
    public void AMachineHoldingThePublicAddressIsNotBehindAnything()
    {
        var nat = NatAssessment.Assess(
            Local("192.168.1.20", "41.234.9.8"), serverSeenPublicIp: Ip("41.234.9.8"),
            gatewayFound: false, mappingOk: false, gatewayExternalIp: null);

        Assert.False(nat.CgnatSuspected);
        Assert.True(nat.Checked, "holding the public address is a positive answer, not a missing one");
        Assert.Equal(NatReachability.Open, nat.Reachability);
    }

    [Fact]
    public void AGatewayReportingAllZeroesOrLoopbackIsTreatedAsSilent()
    {
        foreach (var useless in new[] { IPAddress.Any, IPAddress.Loopback, IPAddress.IPv6Any })
        {
            var nat = NatAssessment.Assess(
                Local("192.168.1.20"), serverSeenPublicIp: Ip("41.234.9.8"),
                gatewayFound: true, mappingOk: true, gatewayExternalIp: useless);

            Assert.False(nat.Checked);
        }
    }

    [Fact]
    public void NoLocalAddressesAtAllDoesNotThrow()
    {
        var nat = NatAssessment.Assess(
            Array.Empty<IPAddress>(), serverSeenPublicIp: Ip("41.234.9.8"),
            gatewayFound: false, mappingOk: false, gatewayExternalIp: null);

        Assert.False(nat.Checked);
        Assert.Equal(NatReachability.Blocked, nat.Reachability);
    }

    // --------------------------------------------------------------- the shape that reaches the server

    [Fact]
    public void TheDiagnosticsRowCarriesWhetherTheQuestionCouldBeAnswered()
    {
        var diagnostics = new GatherDiagnostics(
            UpnpFound: false, MappingOk: false, UpnpExternalIp: null,
            CgnatSuspected: false, Ipv6Global: false, Errors: Array.Empty<string>(),
            CgnatChecked: false, Reachability: NatReachability.Blocked, NatEvidence: "no_gateway_answered");

        var row = diagnostics.ToDictionary();

        Assert.Equal(false, row["cgnat_suspected"]);
        Assert.Equal(false, row["cgnat_checked"]);
        Assert.Equal("blocked", row["nat_reachability"]);
        Assert.Equal("no_gateway_answered", row["nat_evidence"]);
    }
}
