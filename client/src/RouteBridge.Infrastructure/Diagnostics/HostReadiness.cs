using RouteBridge.Core.Net;

namespace RouteBridge.Infrastructure.Diagnostics;

/// <summary>
/// What this machine looks like as a host, in the two terms a user can act on: can the other device reach my listener
/// (the inbound firewall rule), and will the sites see MY address (no VPN holding the outbound route). It is the same
/// object behind the warning on the host page and the readiness summary of the first run, so the two can never disagree.
/// <para>
/// Neither finding blocks anything. The host may go on being available with a missing rule and with a VPN up; the product
/// only owes them an honest description of what will happen (product document section 15).
/// </para>
/// </summary>
/// <param name="FirewallRulePresent">Tri-state: <c>true</c> present, <c>false</c> missing, <c>null</c> could not be determined.</param>
/// <param name="FirewallProfile">Active firewall profile(s), <c>unknown</c> on a localized Windows, <c>null</c> when not determined.</param>
/// <param name="Ipv6Global">This machine has a routable IPv6 address (the <c>v6</c> candidate type is worth trying).</param>
/// <param name="Vpn">Track B's classified VPN result; <see cref="VpnDetectionResult.ShouldWarn"/> is the only thing that warns.</param>
public sealed record HostReadiness(
    bool? FirewallRulePresent,
    string? FirewallProfile,
    bool Ipv6Global,
    VpnDetectionResult Vpn)
{
    /// <summary>Nothing has been probed yet (what the UI binds to before the first probe finishes).</summary>
    public static HostReadiness Unknown { get; } = new(null, null, false, VpnDetectionResult.NotDetected);

    /// <summary>The rule is definitely missing — the one firewall state worth telling the user about.</summary>
    public bool IsFirewallRuleMissing => FirewallRulePresent is false;

    /// <summary>
    /// Warn about the VPN? Only at high confidence, i.e. a VPN adapter that actually holds the outbound route. A VPN
    /// adapter that is merely up (split tunnelling, an idle client) changes nothing about the exit address, and warning
    /// about it would train the user to ignore the warning that matters.
    /// </summary>
    public bool ShouldWarnAboutVpn => Vpn.ShouldWarn;

    /// <summary>The adapter to name in the warning, e.g. <c>Mullvad</c> or <c>utun3</c>; null when there is nothing to warn about.</summary>
    public string? VpnAdapterName => Vpn.ShouldWarn ? (Vpn.AdapterDescription is { Length: > 0 } d ? d : Vpn.AdapterName) : null;

    /// <summary>Nothing to tell the user: the rule is there (or unknowable) and no VPN holds the route.</summary>
    public bool IsClear => !IsFirewallRuleMissing && !ShouldWarnAboutVpn;
}

/// <summary>Answers <see cref="HostReadiness"/> for this machine; injectable so the UI can be driven from a script.</summary>
public interface IHostReadinessProbe
{
    /// <summary>Never throws; anything that cannot be determined comes back as "unknown", not as "no".</summary>
    Task<HostReadiness> InspectAsync(CancellationToken ct);
}
