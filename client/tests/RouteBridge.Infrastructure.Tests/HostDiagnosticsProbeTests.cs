using RouteBridge.Core.Net;
using RouteBridge.Infrastructure.Diagnostics;
using RouteBridge.Infrastructure.Tests.Support;

namespace RouteBridge.Infrastructure.Tests;

/// <summary>
/// The probe that combines the two host checks into the two things the app consumes: the <c>hello.diagnostics</c> frame
/// (docs/ws-protocol.md section 3) and the readiness the user is shown. Both detections are injected, so what is tested
/// here is the decision — above all the one week 6 added: <b>warn about a VPN only when it holds the outbound route</b>.
/// </summary>
public sealed class HostDiagnosticsProbeTests
{
    private static readonly CancellationToken None = CancellationToken.None;

    private static HostDiagnosticsProbe Probe(FakeFirewallDiagnostics? firewall = null, FakeVpnDetector? vpn = null) =>
        new(firewall ?? new FakeFirewallDiagnostics(rulePresent: true, profile: "public"), vpn ?? new FakeVpnDetector());

    // ---- the VPN decision ----

    [Fact]
    public async Task AVpnHoldingTheOutboundRoute_Warns_AndNamesTheAdapter()
    {
        var vpn = new FakeVpnDetector(FakeVpnDetector.Holding("Mullvad", "Mullvad VPN Tunnel"));

        var readiness = await Probe(vpn: vpn).InspectAsync(None);

        Assert.True(readiness.ShouldWarnAboutVpn);
        Assert.Equal("Mullvad VPN Tunnel", readiness.VpnAdapterName);
        Assert.False(readiness.IsClear);
    }

    [Fact]
    public async Task AVpnAdapterThatCarriesNothing_IsNotWorthAWarning()
    {
        // Low confidence: the adapter is up (split tunnelling, an idle client) but the traffic does not go through it.
        // Warning here would make the warning permanent on many machines, and therefore worthless on the one that matters.
        var vpn = new FakeVpnDetector(FakeVpnDetector.Present());

        var readiness = await Probe(vpn: vpn).InspectAsync(None);

        Assert.False(readiness.ShouldWarnAboutVpn);
        Assert.Null(readiness.VpnAdapterName);
        Assert.True(readiness.IsClear);
        Assert.True(readiness.Vpn.IsVpn); // still reported on the wire
    }

    [Fact]
    public async Task NoVpnAtAll_IsClear()
    {
        var readiness = await Probe().InspectAsync(None);

        Assert.False(readiness.ShouldWarnAboutVpn);
        Assert.False(readiness.Vpn.IsVpn);
        Assert.True(readiness.IsClear);
    }

    [Fact]
    public async Task ADetectorThatThrows_IsTreatedAsNoVpn_AndDoesNotFailTheProbe()
    {
        var vpn = new FakeVpnDetector { Throw = new InvalidOperationException("no adapters") };

        var readiness = await Probe(vpn: vpn).InspectAsync(None);

        Assert.False(readiness.ShouldWarnAboutVpn);
        Assert.Equal(VpnDetectionResult.NotDetected, readiness.Vpn);
    }

    // ---- the firewall half, now delegated to Core ----

    [Fact]
    public async Task AMissingFirewallRule_IsReported_AndAnUnknownOneIsNot()
    {
        var missing = await Probe(new FakeFirewallDiagnostics(rulePresent: false, profile: "public")).InspectAsync(None);
        var unknown = await Probe(new FakeFirewallDiagnostics(rulePresent: null)).InspectAsync(None);

        Assert.True(missing.IsFirewallRuleMissing);
        Assert.False(unknown.IsFirewallRuleMissing);
        Assert.True(unknown.IsClear); // nothing to say is not the same as something to warn about
    }

    [Fact]
    public async Task AFirewallCheckThatThrows_LeavesTheValuesUnknown()
    {
        var firewall = new FakeFirewallDiagnostics { Throw = new InvalidOperationException("netsh exploded") };

        var readiness = await Probe(firewall).InspectAsync(None);

        Assert.Null(readiness.FirewallRulePresent);
        Assert.False(readiness.IsFirewallRuleMissing);
    }

    // ---- the wire frame ----

    [Fact]
    public async Task TheWireDictionary_CarriesTheContractsKeys_IncludingVpnAdapterAsABool()
    {
        var diagnostics = await Probe(
                new FakeFirewallDiagnostics(rulePresent: true, profile: "public"),
                new FakeVpnDetector(FakeVpnDetector.Holding()))
            .CollectAsync(None);

        Assert.True((bool)diagnostics["firewall_rule_present"]!);
        Assert.Equal("public", diagnostics["firewall_profile"]);
        Assert.IsType<bool>(diagnostics["ipv6_global"]);

        // The key is frozen as a bool, and it means "a VPN adapter is present", the same flattening track B does.
        Assert.IsType<bool>(diagnostics["vpn_adapter"]);
        Assert.True((bool)diagnostics["vpn_adapter"]!);
    }

    [Fact]
    public async Task APresentButIdleVpn_StillSetsVpnAdapterOnTheWire()
    {
        // The wire key is about presence; only the human-facing warning is about confidence.
        var diagnostics = await Probe(vpn: new FakeVpnDetector(FakeVpnDetector.Present())).CollectAsync(None);

        Assert.True((bool)diagnostics["vpn_adapter"]!);
    }

    [Fact]
    public async Task NoVpn_SendsFalseRatherThanOmittingTheKey()
    {
        var diagnostics = await Probe().CollectAsync(None);

        Assert.False((bool)diagnostics["vpn_adapter"]!);
    }

    [Fact]
    public async Task ValuesThatCouldNotBeDetermined_AreLeftOutOfTheFrameEntirely()
    {
        // "we did not look" must not reach the server as "we looked and found nothing".
        var diagnostics = await Probe(new FakeFirewallDiagnostics(rulePresent: null)).CollectAsync(None);

        Assert.False(diagnostics.ContainsKey("firewall_rule_present"));
        Assert.False(diagnostics.ContainsKey("firewall_profile"));
        Assert.True(diagnostics.ContainsKey("ipv6_global"));
    }

    [Fact]
    public void Ipv6Global_IsAnswerableOnThisMachine_AndNeverThrows()
    {
        // Whatever this machine has, the probe must return a definite answer without touching the network.
        var probe = new HostDiagnosticsProbe();

        var first = probe.HasGlobalIpv6();
        var second = probe.HasGlobalIpv6();

        Assert.Equal(first, second);
    }

    [Fact]
    public async Task TheDefaultProbe_UsesTheRealDetectionsWithoutThrowing()
    {
        // No injection at all: the constructor's defaults must be usable on any machine the tests run on.
        var readiness = await new HostDiagnosticsProbe().InspectAsync(None);

        Assert.NotNull(readiness.Vpn);
    }
}
