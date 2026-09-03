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
}
