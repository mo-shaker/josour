namespace Josour.Infrastructure;

/// <summary>
/// Per-user data locations. Everything Josour persists lives under <c>%LOCALAPPDATA%\Josour</c>
/// (on non-Windows development machines this maps to the platform's local-application-data folder).
/// </summary>
public static class AppPaths
{
    public const string ProductFolderName = "Josour";

    /// <summary><c>%LOCALAPPDATA%\Josour</c>.</summary>
    public static string LocalAppDataRoot =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.Create),
            ProductFolderName);

    /// <summary><c>%LOCALAPPDATA%\Josour\logs</c>.</summary>
    public static string LogsDirectory => Path.Combine(LocalAppDataRoot, "logs");

    /// <summary><c>%LOCALAPPDATA%\Josour\secrets</c>.</summary>
    public static string SecretsDirectory => Path.Combine(LocalAppDataRoot, "secrets");

    /// <summary><c>%LOCALAPPDATA%\Josour\settings.json</c> (see <c>Settings.AppSettingsStore</c>).</summary>
    public static string SettingsFile => Path.Combine(LocalAppDataRoot, "settings.json");

    /// <summary>
    /// <c>%LOCALAPPDATA%\Josour\auto-accept.json</c>: where a 1.x app kept its trusted-guest rules (ADR-0012). Retired by
    /// ADR-0014 - trust is held per contact on the server - and only named so the app can delete what was left behind.
    /// </summary>
    public static string AutoAcceptFile => Path.Combine(LocalAppDataRoot, "auto-accept.json");

    /// <summary>
    /// <c>%LOCALAPPDATA%\Josour\system-proxy.json</c>: the settings to put back while a session points the system proxy at
    /// this app (see <c>SystemProxy.SystemProxyGuard</c>). Present only while the change is in force.
    /// </summary>
    public static string SystemProxyMarkerFile => Path.Combine(LocalAppDataRoot, "system-proxy.json");

    /// <summary>
    /// <c>%LOCALAPPDATA%\Josour\work-browser.json</c> and <c>BrowserProfile</c>: what a 1.x app kept for its work browser,
    /// retired by ADR-0015 and only named so the app can delete what was left behind.
    /// </summary>
    public static string LegacyWorkBrowserMarkerFile => Path.Combine(LocalAppDataRoot, "work-browser.json");

    public static string LegacyBrowserProfileDirectory => Path.Combine(LocalAppDataRoot, "BrowserProfile");
}
