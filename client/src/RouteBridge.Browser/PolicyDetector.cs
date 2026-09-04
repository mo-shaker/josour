using RouteBridge.Browser.Registry;
using RouteBridge.Core.Browser;

namespace RouteBridge.Browser;

/// <param name="Findings">مثل "HKLM:ProxyMode=system" للتشخيص.</param>
public sealed record BrowserPolicyStatus(bool ProxyManaged, bool UserDataDirManaged, IReadOnlyList<string> Findings)
{
    public bool AnyManaged => ProxyManaged || UserDataDirManaged;
    public static readonly BrowserPolicyStatus Unmanaged = new(false, false, Array.Empty<string>());
}

/// <summary>
/// يكشف سياسات المؤسسة التي تُبطل --proxy-server أو --user-data-dir: HKLM/HKCU SOFTWARE\Policies\Google\Chrome و\Microsoft\Edge،
/// القيم ProxySettings / ProxyMode / ProxyServer / UserDataDir. أي قيمة موجودة (ولو ProxyMode=system) = مُدار، لأن السياسة تتقدم على سطر الأوامر.
/// </summary>
public static class PolicyDetector
{
    private static readonly string[] ProxyValues = { "ProxySettings", "ProxyMode", "ProxyServer" };

    public static string PolicyKey(BrowserKind kind)
        => kind == BrowserKind.Chrome ? @"SOFTWARE\Policies\Google\Chrome" : @"SOFTWARE\Policies\Microsoft\Edge";

    public static BrowserPolicyStatus Detect(BrowserKind kind, IRegistryReader registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        var key = PolicyKey(kind);
        var findings = new List<string>();
        var proxy = false;
        var userDataDir = false;
        foreach (var root in new[] { RegistryRoot.LocalMachine, RegistryRoot.CurrentUser })
        {
            var rootName = root == RegistryRoot.LocalMachine ? "HKLM" : "HKCU";
            foreach (var value in ProxyValues)
            {
                var v = registry.GetString(root, key, value);
                if (v is null) continue;
                proxy = true;
                findings.Add($"{rootName}:{value}={Truncate(v)}");
            }
            var dir = registry.GetString(root, key, "UserDataDir");
            if (dir is not null)
            {
                userDataDir = true;
                findings.Add($"{rootName}:UserDataDir={Truncate(dir)}");
            }
        }
        return new BrowserPolicyStatus(proxy, userDataDir, findings);
    }

    private static string Truncate(string s) => s.Length <= 80 ? s : s[..77] + "...";
}
