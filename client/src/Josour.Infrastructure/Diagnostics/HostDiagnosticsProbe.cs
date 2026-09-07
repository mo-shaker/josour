using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Josour.Core.Diagnostics;
using Josour.Core.Net;

namespace Josour.Infrastructure.Diagnostics;

/// <summary>
/// Everything the app needs to know about this machine before it offers itself as a host: the two <c>hello.diagnostics</c>
/// values this layer answers (<c>firewall_rule_present</c>, <c>firewall_profile</c>, plus <c>ipv6_global</c>) and, from week 6,
/// the VPN verdict behind the host's warning.
/// <para>
/// <b>Neither detection is implemented here any more.</b> The firewall check moved to
/// <see cref="FirewallDiagnostics"/> in <c>Josour.Core</c> because this class and track B's
/// <c>Josour.Tunnel.Diagnostics.HostDiagnostics</c> both shelled out to netsh with slightly different arguments and
/// parsers; VPN detection was always track B's (<see cref="VpnDetector"/>), reached through
/// <see cref="IVpnDetector"/> so that the app can hand in <c>HostDiagnostics.DetectVpn()</c> and the tests a scripted result.
/// This class is now the place where those answers are combined into the two things the app consumes:
/// <see cref="CollectAsync"/> (the wire frame) and <see cref="InspectAsync"/> (the readiness the user is shown).
/// </para>
/// </summary>
public sealed class HostDiagnosticsProbe : IHostReadinessProbe
{
    /// <summary>The rule the installer adds; kept as an alias so existing call sites and log lines do not move.</summary>
    public const string FirewallRuleName = FirewallDiagnostics.RuleName;

    /// <summary>What a profile that could not be determined is reported as.</summary>
    public const string UnknownProfile = FirewallDiagnostics.UnknownProfile;

    private readonly IFirewallDiagnostics _firewall;
    private readonly IVpnDetector _vpn;
    private readonly ILogger _logger;

    public HostDiagnosticsProbe(
        IFirewallDiagnostics? firewall = null,
        IVpnDetector? vpn = null,
        ILogger<HostDiagnosticsProbe>? logger = null)
    {
        _firewall = firewall ?? FirewallDiagnostics.System;
        _vpn = vpn ?? SystemVpnDetector.Instance;
        _logger = (ILogger?)logger ?? NullLogger.Instance;
    }

    /// <summary>
    /// The diagnostics as the <c>hello</c> frame carries them. Keys are the contract's, values are JSON-friendly, and a
    /// value that could not be determined is left OUT rather than sent as null — the server stores whatever arrives, and
    /// "we did not look" should not read as "we looked and found nothing". Never throws.
    /// </summary>
    public async Task<Dictionary<string, object?>> CollectAsync(CancellationToken ct)
    {
        var readiness = await InspectAsync(ct).ConfigureAwait(false);
        var diagnostics = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["ipv6_global"] = readiness.Ipv6Global,

            // docs/ws-protocol.md freezes vpn_adapter as a bool, so the classified result is flattened the same way track B
            // flattens it: PRESENT (any confidence), not "should warn". The confidence only decides whether the human is told.
            ["vpn_adapter"] = readiness.Vpn.IsVpn,
        };

        if (readiness.FirewallRulePresent is bool present)
        {
            diagnostics["firewall_rule_present"] = present;
        }

        if (!string.IsNullOrEmpty(readiness.FirewallProfile))
        {
            diagnostics["firewall_profile"] = readiness.FirewallProfile;
        }

        return diagnostics;
    }

    /// <summary>The raw findings, for the readiness summary and the host's warnings. Never throws.</summary>
    public async Task<HostReadiness> InspectAsync(CancellationToken ct)
    {
        var ipv6 = HasGlobalIpv6();

        FirewallStatus firewall;
        try
        {
            firewall = await _firewall.InspectAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "The firewall check failed; its values stay unknown");
            firewall = FirewallStatus.Unknown;
        }

        if (firewall.RuleMissing)
        {
            _logger.LogInformation(
                "No inbound firewall rule named '{RuleName}' (profile {Profile}): the listener may be blocked silently",
                FirewallRuleName,
                firewall.Profile ?? UnknownProfile);
        }
        else if (firewall.RulePresent is null)
        {
            _logger.LogDebug("netsh could not be run: firewall_rule_present stays unknown");
        }

        VpnDetectionResult vpn;
        try
        {
            vpn = _vpn.Detect() ?? VpnDetectionResult.NotDetected;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "VPN detection failed; treating this machine as not behind a VPN");
            vpn = VpnDetectionResult.NotDetected;
        }

        if (vpn.ShouldWarn)
        {
            _logger.LogWarning(
                "A VPN adapter holds the outbound route ({Vpn}): sites will see the VPN's exit address, not this machine's",
                vpn.Describe());
        }
        else if (vpn.IsVpn)
        {
            _logger.LogInformation("A VPN adapter is present but does not hold the outbound route ({Vpn}): no warning", vpn.Describe());
        }

        return new HostReadiness(firewall.RulePresent, firewall.Profile, ipv6, vpn);
    }

    /// <summary>
    /// A global unicast IPv6 address on an interface that is up — not a link-local (<c>fe80::</c>), not a unique-local
    /// (<c>fc00::/7</c>) and not the loopback: only a routable address makes the <c>v6</c> candidate type worth trying.
    /// </summary>
    public bool HasGlobalIpv6()
    {
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                try
                {
                    if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                    {
                        continue;
                    }

                    foreach (var address in nic.GetIPProperties().UnicastAddresses)
                    {
                        var ip = address.Address;
                        if (ip.AddressFamily != AddressFamily.InterNetworkV6)
                        {
                            continue;
                        }

                        if (ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6Teredo || IPAddress.IsLoopback(ip) || IsUniqueLocal(ip))
                        {
                            continue;
                        }

                        return true;
                    }
                }
                catch (Exception ex) when (ex is NetworkInformationException or PlatformNotSupportedException)
                {
                    // one interface refused to describe itself; the others still count
                }
            }
        }
        catch (Exception ex) when (ex is NetworkInformationException or PlatformNotSupportedException)
        {
            _logger.LogDebug("The network interfaces could not be enumerated: {Message}", ex.Message);
        }

        return false;
    }

    /// <summary><c>fc00::/7</c>: private addressing, not reachable from the other device's network.</summary>
    private static bool IsUniqueLocal(IPAddress address) => (address.GetAddressBytes()[0] & 0xFE) == 0xFC;
}
