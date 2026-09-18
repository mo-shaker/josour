using System.Runtime.Versioning;
using System.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace Josour.App.Services;

/// <summary>
/// "Start Josour when I sign in", per platform.
/// <para>
/// Windows: the <c>HKCU\Software\Microsoft\Windows\CurrentVersion\Run</c> value <c>Josour</c> =
/// <c>"&lt;exe path&gt;" --minimized</c>. The installer's optional "autostart" task writes the same value, so both stay
/// in sync.
/// </para>
/// <para>
/// macOS: a LaunchAgent property list at <c>~/Library/LaunchAgents/com.josour.client.plist</c> with
/// <c>RunAtLoad</c>. Nothing is handed to <c>launchctl</c>: launchd reads the folder at login, which is the only moment
/// this setting is about, and loading it now would start a second copy beside the one the user is looking at.
/// </para>
/// <para>Anywhere else the setting is unsupported and the settings window says so rather than pretending.</para>
/// </summary>
public sealed class StartupRegistration : IStartupRegistration
{
    public const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string ValueName = "Josour";

    /// <summary>The LaunchAgent label, which is also its file name. Reverse-DNS, as launchd expects.</summary>
    public const string LaunchAgentLabel = "com.josour.client";

    private readonly ILogger<StartupRegistration> _logger;

    public StartupRegistration(ILogger<StartupRegistration> logger)
    {
        _logger = logger;
    }

    public bool IsSupported => OperatingSystem.IsWindows() || OperatingSystem.IsMacOS();

    public bool IsEnabled
    {
        get
        {
            try
            {
                if (OperatingSystem.IsWindows())
                {
                    return ReadCommand() is not null;
                }

                return OperatingSystem.IsMacOS() && File.Exists(LaunchAgentPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
            {
                // "I could not tell" reads as off; the user can switch it on again, which is the harmless direction.
                _logger.LogWarning(ex, "Could not read the start-at-login setting");
                return false;
            }
        }
    }

    public void SetEnabled(bool enabled)
    {
        if (!IsSupported)
        {
            _logger.LogWarning("Start at login is not available on this platform");
            return;
        }

        if (OperatingSystem.IsWindows())
        {
            if (enabled)
            {
                WriteCommand(BuildCommandLine(ExecutablePath));
            }
            else
            {
                DeleteCommand();
            }
        }
        else if (OperatingSystem.IsMacOS())
        {
            SetEnabledMac(enabled);
        }

        _logger.LogInformation("Start at login {State}", enabled ? "enabled" : "disabled");
    }

    /// <summary>Exactly what the Run value contains: the quoted exe path plus <c>--minimized</c>.</summary>
    public static string BuildCommandLine(string exePath) => $"\"{exePath}\" {StartupOptions.MinimizedSwitch}";

    /// <summary><c>~/Library/LaunchAgents/com.josour.client.plist</c>.</summary>
    public static string LaunchAgentPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "Library",
        "LaunchAgents",
        LaunchAgentLabel + ".plist");

    /// <summary>
    /// The LaunchAgent document. Each argument is its own <c>&lt;string&gt;</c>, never one shell-quoted line: launchd
    /// executes the program directly, so a path with a space in it — <c>/Applications/Josour App/…</c> — would
    /// otherwise be read as two arguments and the agent would simply never start.
    /// </summary>
    public static string BuildLaunchAgent(string exePath) =>
        $"""
         <?xml version="1.0" encoding="UTF-8"?>
         <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
         <plist version="1.0">
         <dict>
             <key>Label</key>
             <string>{LaunchAgentLabel}</string>
             <key>ProgramArguments</key>
             <array>
                 <string>{SecurityElement.Escape(exePath)}</string>
                 <string>{StartupOptions.MinimizedSwitch}</string>
             </array>
             <key>RunAtLoad</key>
             <true/>
             <key>ProcessType</key>
             <string>Interactive</string>
         </dict>
         </plist>

         """;

    private static string ExecutablePath =>
        Environment.ProcessPath ?? throw new InvalidOperationException("Cannot determine the Josour executable path.");

    [SupportedOSPlatform("macos")]
    private void SetEnabledMac(bool enabled)
    {
        var path = LaunchAgentPath;
        try
        {
            if (!enabled)
            {
                File.Delete(path);
                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, BuildLaunchAgent(ExecutablePath));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "Could not write the LaunchAgent at {Path}", path);
        }
    }

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
