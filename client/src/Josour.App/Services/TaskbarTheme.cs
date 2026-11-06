using System.Runtime.Versioning;
using Microsoft.Win32;

namespace Josour.App.Services;

/// <summary>
/// Whether the Windows taskbar is light (Phase 1 decision 6): the tray glyph is ink on a light taskbar and white on a dark one,
/// like Windows' own icons. The taskbar follows "Windows mode" (<c>SystemUsesLightTheme</c>), which is not the apps' mode, so
/// it is read from the registry. A UI-only read; nothing is written.
/// </summary>
public static class TaskbarTheme
{
    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    /// <summary>True for a light taskbar; false for a dark one, and everywhere but Windows (where it does not apply).</summary>
    public static bool IsLight => OperatingSystem.IsWindows() && ReadIsLight();

    [SupportedOSPlatform("windows")]
    private static bool ReadIsLight()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
            return key?.GetValue("SystemUsesLightTheme") is int value && value != 0;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return false; // Windows' default taskbar is dark
        }
    }
}
