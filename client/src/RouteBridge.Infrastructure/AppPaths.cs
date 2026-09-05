namespace RouteBridge.Infrastructure;

/// <summary>
/// Per-user data locations. Everything RouteBridge persists lives under <c>%LOCALAPPDATA%\RouteBridge</c>
/// (on non-Windows development machines this maps to the platform's local-application-data folder).
/// </summary>
public static class AppPaths
{
    public const string ProductFolderName = "RouteBridge";

    /// <summary><c>%LOCALAPPDATA%\RouteBridge</c>.</summary>
    public static string LocalAppDataRoot =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.Create),
            ProductFolderName);

    /// <summary><c>%LOCALAPPDATA%\RouteBridge\logs</c>.</summary>
    public static string LogsDirectory => Path.Combine(LocalAppDataRoot, "logs");

    /// <summary><c>%LOCALAPPDATA%\RouteBridge\secrets</c>.</summary>
    public static string SecretsDirectory => Path.Combine(LocalAppDataRoot, "secrets");

    /// <summary><c>%LOCALAPPDATA%\RouteBridge\settings.json</c> (see <c>Settings.AppSettingsStore</c>).</summary>
    public static string SettingsFile => Path.Combine(LocalAppDataRoot, "settings.json");

    /// <summary>
    /// <c>%LOCALAPPDATA%\RouteBridge\work-browser.json</c>: the note that says a work browser is running, so a start-up
    /// after a crash can close it (see <c>Session.StaleWorkBrowserGuard</c>). Present only while a browser is up.
    /// </summary>
    public static string WorkBrowserMarkerFile => Path.Combine(LocalAppDataRoot, "work-browser.json");
}
