using System.Diagnostics;
using System.Runtime.InteropServices;
using RouteBridge.Core.Net;
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
        else
        {
            // خارج Windows: متغيرات البيئة (http_proxy وأخواتها) عبر نفس المُحلّل الذي يستعمله المسار المباشر.
            // لا يُستعمل على Windows لأن WinHTTP قد يجلب ملف PAC عبر الشبكة، والتشخيص لا يحتمل انتظارًا.
            systemProxy = EnvironmentProxyPresent();
        }

        return new Dictionary<string, object?>
        {
            ["firewall_rule_present"] = firewallRule,
            ["firewall_profile"] = firewallProfile,
            // مفاتيح hello.diagnostics مجمَّدة في docs/ws-protocol.md، فتبقى bool؛ التصنيف الكامل عبر DetectVpn().
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
    /// الكشف المصنَّف عن الـ VPN (الخطة 8.5): أي واجهة، وبأي ثقة، وهل تحمل مسار الخروج.
    /// هذا ما يستهلكه المسار C للتحذير: <c>ShouldWarn</c> صحيح للثقة العالية وحدها.
    /// القواعد والاستثناءات في <see cref="VpnDetector"/>. لا يرمي أبدًا.
    /// </summary>
    public static VpnDetectionResult DetectVpn() => DetectVpn(SystemNetworkAdapterSource.Instance);

    /// <inheritdoc cref="DetectVpn()"/>
    public static VpnDetectionResult DetectVpn(INetworkAdapterSource source)
    {
        try { return VpnDetector.Detect(source); }
        catch { return VpnDetectionResult.NotDetected; }
    }

    /// <summary>مفتاح <c>vpn_adapter</c> في hello.diagnostics: هل هناك واجهة VPN عاملة (بأي ثقة).</summary>
    public static bool VpnAdapterPresent() => DetectVpn().IsVpn;

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

    /// <summary>خارج Windows: متغيرات البيئة فقط (بلا شبكة). null إن تعذّر القرار.</summary>
    private static bool? EnvironmentProxyPresent()
    {
        try { return SystemProxyResolver.Default.IsConfigured; }
        catch { return null; }
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
