using System.Runtime.InteropServices;
using RouteBridge.Core.Diagnostics;
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
    /// <summary>اسم قاعدة الجدار الناري التي يضيفها المثبّت. المصدر الوحيد في <see cref="FirewallDiagnostics"/>.</summary>
    public const string FirewallRuleName = FirewallDiagnostics.RuleName;

    public static async Task<Dictionary<string, object?>> CollectAsync(CancellationToken ct)
    {
        bool? firewallRule = null;
        string? firewallProfile = null;
        bool? systemProxy = null;

        if (OperatingSystem.IsWindows())
        {
            // فحص الجدار الناري صار في RouteBridge.Core.Diagnostics ليكون له مالك واحد:
            // كان هنا وفي RouteBridge.Infrastructure نسختان من استدعاء netsh وتفسير نصه.
            var firewall = await FirewallDiagnostics.InspectSystemAsync(ct).ConfigureAwait(false);
            firewallRule = firewall.RulePresent;
            firewallProfile = firewall.Profile;
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


    private static bool SafeBool(Func<bool> probe)
    {
        try { return probe(); } catch { return false; }
    }
}
