using System.Runtime.InteropServices;
using Josour.Core.Diagnostics;
using Josour.Core.Net;
using Josour.Tunnel.Candidates;

namespace Josour.Tunnel.Diagnostics;

/// <summary>
/// The "when available is enabled" diagnostics, under the docs/ws-protocol.md hello.diagnostics keys literally:
/// firewall_rule_present, firewall_profile, vpn_adapter, system_proxy_present, os_build, ipv6_global.
/// Values that cannot be determined on this platform are null. It never throws.
/// </summary>
public static class HostDiagnostics
{
    /// <summary>The name of the firewall rule the installer adds. The single source is <see cref="FirewallDiagnostics"/>.</summary>
    public const string FirewallRuleName = FirewallDiagnostics.RuleName;

    public static async Task<Dictionary<string, object?>> CollectAsync(CancellationToken ct)
    {
        bool? firewallRule = null;
        string? firewallProfile = null;
        bool? systemProxy = null;

        if (OperatingSystem.IsWindows())
        {
            // The firewall check moved into Josour.Core.Diagnostics so it has one owner:
            // there used to be two copies here and in Josour.Infrastructure of the netsh invocation and of reading its text.
            var firewall = await FirewallDiagnostics.InspectSystemAsync(ct).ConfigureAwait(false);
            firewallRule = firewall.RulePresent;
            firewallProfile = firewall.Profile;
            systemProxy = SystemProxyPresent();
        }
        else
        {
            // Off Windows: the environment variables (http_proxy and its siblings) through the same resolver the direct path uses.
            // Not used on Windows because WinHTTP may fetch a PAC file over the network, and the diagnostics cannot bear a wait.
            systemProxy = EnvironmentProxyPresent();
        }

        return new Dictionary<string, object?>
        {
            ["firewall_rule_present"] = firewallRule,
            ["firewall_profile"] = firewallProfile,
            // The hello.diagnostics keys are frozen in docs/ws-protocol.md, so this stays a bool; the full classification is through DetectVpn().
            ["vpn_adapter"] = DetectVpn().IsVpn,
            ["system_proxy_present"] = systemProxy,
            ["os_build"] = OsBuild(),
            ["ipv6_global"] = SafeBool(LocalNetwork.HasGlobalIPv6),
        };
    }

    public static string OsBuild()
    {
        try { return RuntimeInformation.OSDescription; }
        catch { return Environment.OSVersion.VersionString; }
    }

    /// <summary>
    /// The classified VPN detection (plan 8.5): which interface, at what confidence, and whether it holds the egress route.
    /// This is what track C consumes for the warning: <c>ShouldWarn</c> is true for high confidence alone.
    /// The rules and the exclusions are in <see cref="VpnDetector"/>. It never throws.
    /// </summary>
    public static VpnDetectionResult DetectVpn() => DetectVpn(SystemNetworkAdapterSource.Instance);

    /// <inheritdoc cref="DetectVpn()"/>
    public static VpnDetectionResult DetectVpn(INetworkAdapterSource source)
    {
        try { return VpnDetector.Detect(source); }
        catch { return VpnDetectionResult.NotDetected; }
    }

    /// <summary>The <c>vpn_adapter</c> key in hello.diagnostics: is there a VPN interface that is up (at any confidence).</summary>
    public static bool VpnAdapterPresent() => DetectVpn().IsVpn;



    // WINDOWS-ONLY: HKCU\Software\Microsoft\Windows\CurrentVersion\Internet Settings\ProxyEnable (DWORD).
    private static bool? SystemProxyPresent()
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Internet Settings");
            if (key is null) return false;
            var enabled = key.GetValue("ProxyEnable");
            return enabled is int i && i != 0;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Off Windows: the environment variables only (with no network). null if the decision cannot be made.</summary>
    private static bool? EnvironmentProxyPresent()
    {
        try { return SystemProxyResolver.Default.IsConfigured; }
        catch { return null; }
    }


    private static bool SafeBool(Func<bool> probe)
    {
        try { return probe(); } catch { return false; }
    }
}
