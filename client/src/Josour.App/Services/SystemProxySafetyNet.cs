using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;
using System.Security;
using Josour.Infrastructure.SystemProxy;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Win32;

namespace Josour.App.Services;

/// <summary>
/// What puts the system proxy back when this process cannot (ADR-0015 point 2):
/// <list type="bullet">
///   <item>a guardian — this same executable with <c>--proxy-guardian</c>, no window — that restores the moment the app dies;</item>
///   <item>a restore at the next login for a power cut or a forced log-off: a one-shot <c>RunOnce</c> entry on Windows, and on
///     macOS a launch agent that runs <c>--restore-proxy</c> at every login (it does nothing when there is no marker, and is
///     written once so macOS announces a new background item once, not every session).</item>
/// </list>
/// The app's own start-up and "Repair internet settings" in the tray are the remaining two.
/// </summary>
public sealed class SystemProxySafetyNet : ISystemProxySafetyNet
{
    public const string GuardianSwitch = "--proxy-guardian";
    public const string RestoreSwitch = "--restore-proxy";

    private const string RunOnceKey = @"Software\Microsoft\Windows\CurrentVersion\RunOnce";
    private const string RunOnceValue = "JosourRestoreProxy";
    private const string LaunchAgentLabel = "com.josour.proxy-restore";

    private readonly ILogger _logger;
    private readonly string? _executable;

    public SystemProxySafetyNet(ILogger<SystemProxySafetyNet>? logger = null, string? executable = null)
    {
        _logger = (ILogger?)logger ?? NullLogger.Instance;
        _executable = executable ?? Environment.ProcessPath;
    }

    public static string LaunchAgentPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "LaunchAgents", LaunchAgentLabel + ".plist");

    public void Arm(SystemProxyRunMarker marker)
    {
        ArgumentNullException.ThrowIfNull(marker);
        if (string.IsNullOrEmpty(_executable))
        {
            _logger.LogWarning("The executable's path is unknown: no guardian and no restore at login for this session");
            return;
        }

        StartGuardian(marker);
        try
        {
            if (OperatingSystem.IsWindows()) SetRunOnce();
            else if (OperatingSystem.IsMacOS()) EnsureLaunchAgent();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "The restore at the next login could not be registered");
        }
    }

    public void Disarm()
    {
        try
        {
            if (OperatingSystem.IsWindows()) ClearRunOnce();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "The restore at the next login could not be removed");
        }
    }

    private void StartGuardian(SystemProxyRunMarker marker)
    {
        var info = new ProcessStartInfo(_executable!)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        info.ArgumentList.Add(GuardianSwitch);
        info.ArgumentList.Add(marker.ProcessId.ToString(CultureInfo.InvariantCulture));
        info.ArgumentList.Add(marker.ProcessStartedAt.UtcTicks.ToString(CultureInfo.InvariantCulture));
        try
        {
            using var guardian = Process.Start(info);
            _logger.LogInformation("System-proxy guardian started ({GuardianPid})", guardian?.Id);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "The system-proxy guardian could not be started; a crash now leaves the restore to the next start");
        }
    }

    [SupportedOSPlatform("windows")]
    private void SetRunOnce()
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunOnceKey);
        key.SetValue(RunOnceValue, $"\"{_executable}\" {RestoreSwitch}", RegistryValueKind.String);
    }

    [SupportedOSPlatform("windows")]
    private static void ClearRunOnce()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunOnceKey, writable: true);
        key?.DeleteValue(RunOnceValue, throwOnMissingValue: false);
    }

    private void EnsureLaunchAgent()
    {
        var plist = BuildLaunchAgent(_executable!);
        var path = LaunchAgentPath;
        if (File.Exists(path) && File.ReadAllText(path) == plist) return;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, plist);
        _logger.LogInformation("Registered the system-proxy restore at login ({Path})", path);
    }

    public static string BuildLaunchAgent(string executable) => $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
        <plist version="1.0">
        <dict>
            <key>Label</key>
            <string>{LaunchAgentLabel}</string>
            <key>ProgramArguments</key>
            <array>
                <string>{SecurityElement.Escape(executable)}</string>
                <string>{RestoreSwitch}</string>
            </array>
            <key>RunAtLoad</key>
            <true/>
            <key>ProcessType</key>
            <string>Background</string>
        </dict>
        </plist>
        """;
}
