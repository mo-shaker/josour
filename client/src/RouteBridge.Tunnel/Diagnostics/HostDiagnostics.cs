using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using RouteBridge.Tunnel.Candidates;

namespace RouteBridge.Tunnel.Diagnostics;

/// <summary>
/// تشخيص «عند تفعيل متاح» بمفاتيح docs/ws-protocol.md hello.diagnostics حرفيًا:
/// firewall_rule_present, firewall_profile, vpn_adapter, system_proxy_present, os_build, ipv6_global.
/// القيم غير القابلة للتحديد على هذه المنصة تكون null. لا يرمي أبدًا.
/// </summary>
public static class HostDiagnostics
{
    public const string FirewallRuleName = "RouteBridge Tunnel";
    private static readonly TimeSpan ProcessTimeout = TimeSpan.FromSeconds(3);
    private static readonly string[] VpnMarkers = { "VPN", "WireGuard", "Tailscale" };

    public static async Task<Dictionary<string, object?>> CollectAsync(CancellationToken ct)
    {
        bool? firewallRule = null;
        string? firewallProfile = null;
        bool? systemProxy = null;

        if (OperatingSystem.IsWindows())
        {
            firewallRule = await FirewallRulePresentAsync(ct).ConfigureAwait(false);
            firewallProfile = await FirewallProfileAsync(ct).ConfigureAwait(false);
            systemProxy = SystemProxyPresent();
        }

        return new Dictionary<string, object?>
        {
            ["firewall_rule_present"] = firewallRule,
            ["firewall_profile"] = firewallProfile,
            ["vpn_adapter"] = VpnAdapterPresent(),
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

    /// <summary>واجهة من نوع Tunnel أو يحتوي اسمها/وصفها VPN أو WireGuard أو Tailscale، وهي عاملة.</summary>
    public static bool VpnAdapterPresent()
    {
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                try
                {
                    if (nic.OperationalStatus != OperationalStatus.Up) continue;
                    if (nic.NetworkInterfaceType == NetworkInterfaceType.Tunnel) return true;
                    if (VpnMarkers.Any(m => nic.Description.Contains(m, StringComparison.OrdinalIgnoreCase) || nic.Name.Contains(m, StringComparison.OrdinalIgnoreCase)))
                        return true;
                }
                catch { /* واجهة واحدة */ }
            }
        }
        catch { /* تعداد */ }
        return false;
    }

    // WINDOWS-ONLY: يُنفَّذ netsh ويُفسَّر نصه؛ لم يُشغَّل على Windows بعد. النص مترجَم على أنظمة غير إنجليزية،
    // لذا نعتمد على رمز الخروج (0 = وُجدت قاعدة) أكثر من النص.
    private static async Task<bool?> FirewallRulePresentAsync(CancellationToken ct)
    {
        var (exitCode, output) = await RunAsync("netsh", $"advfirewall firewall show rule name=\"{FirewallRuleName}\"", ct).ConfigureAwait(false);
        if (exitCode is null) return null;
        if (exitCode == 0 && output.Contains(FirewallRuleName, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    // WINDOWS-ONLY: "netsh advfirewall show currentprofile" يبدأ بسطر مثل "Public Profile Settings:" (قد يتعدد عند عدة شبكات).
    private static async Task<string?> FirewallProfileAsync(CancellationToken ct)
    {
        var (exitCode, output) = await RunAsync("netsh", "advfirewall show currentprofile", ct).ConfigureAwait(false);
        if (exitCode is null) return null;
        var profiles = new List<string>();
        foreach (var rawLine in output.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Contains("Profile Settings", StringComparison.OrdinalIgnoreCase))
            {
                var word = line.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
                if (!string.IsNullOrEmpty(word)) profiles.Add(word.ToLowerInvariant());
            }
        }
        if (profiles.Count > 0) return string.Join(",", profiles.Distinct());
        var first = output.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0);
        return first?.TrimEnd(':');
    }

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

    private static async Task<(int? ExitCode, string Output)> RunAsync(string fileName, string arguments, CancellationToken ct)
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo(fileName, arguments)
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                },
            };
            if (!process.Start()) return (null, string.Empty);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(ProcessTimeout);
            var stdout = process.StandardOutput.ReadToEndAsync(cts.Token);
            try
            {
                await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch { /* تجاهل */ }
                return (null, string.Empty);
            }
            return (process.ExitCode, await stdout.ConfigureAwait(false));
        }
        catch
        {
            return (null, string.Empty);
        }
    }

    private static bool SafeBool(Func<bool> probe)
    {
        try { return probe(); } catch { return false; }
    }
}
