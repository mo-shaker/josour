using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace RouteBridge.App.Services;

/// <summary>
/// Reads/writes <c>HKCU\Software\Microsoft\Windows\CurrentVersion\Run</c> value <c>RouteBridge</c> =
/// <c>"&lt;exe path&gt;" --minimized</c>. The installer's optional "autostart" task writes the same value, so both stay in sync.
/// </summary>
public sealed class StartupRegistration : IStartupRegistration
{
    public const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string ValueName = "RouteBridge";

    private readonly ILogger<StartupRegistration> _logger;

    public StartupRegistration(ILogger<StartupRegistration> logger)
    {
        _logger = logger;
    }

    public bool IsSupported => OperatingSystem.IsWindows();

    public bool IsEnabled => OperatingSystem.IsWindows() && ReadCommand() is not null;

    public void SetEnabled(bool enabled)
    {
        if (!OperatingSystem.IsWindows())
        {
            _logger.LogWarning("Start with Windows is only available on Windows");
            return;
        }

        if (enabled)
        {
            WriteCommand(BuildCommandLine(ExecutablePath));
        }
        else
        {
            DeleteCommand();
        }

        _logger.LogInformation("Start with Windows {State}", enabled ? "enabled" : "disabled");
    }

    /// <summary>Exactly what the Run value contains: the quoted exe path plus <c>--minimized</c>.</summary>
    public static string BuildCommandLine(string exePath) => $"\"{exePath}\" {StartupOptions.MinimizedSwitch}";

    private static string ExecutablePath =>
        Environment.ProcessPath ?? throw new InvalidOperationException("Cannot determine the RouteBridge executable path.");

    [SupportedOSPlatform("windows")]
    private static string? ReadCommand()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
        return key?.GetValue(ValueName) as string;
    }

    [SupportedOSPlatform("windows")]
    private static void WriteCommand(string command)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
        key.SetValue(ValueName, command, RegistryValueKind.String);
    }

    [SupportedOSPlatform("windows")]
    private static void DeleteCommand()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
        key?.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}
