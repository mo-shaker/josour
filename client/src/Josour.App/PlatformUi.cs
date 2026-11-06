using Avalonia.Input;

namespace Josour.App;

/// <summary>
/// Each system's conventions where the views need them (docs/design/desktop-phase2-mockups.html, "Platform conventions"):
/// the command key (Ctrl on Windows, ⌘ on macOS) and whether the main window carries its own More menu (Windows) or leaves
/// those commands to the app menu (macOS).
/// </summary>
public static class PlatformUi
{
    public static bool IsMac => OperatingSystem.IsMacOS();

    /// <summary>The ⋯ menu in the main window's app bar: everywhere but macOS, which has an app menu instead.</summary>
    public static bool HasMoreMenu => !IsMac;

    public static KeyModifiers CommandModifier => IsMac ? KeyModifiers.Meta : KeyModifiers.Control;

    /// <summary>Ctrl+, or ⌘, : Settings.</summary>
    public static KeyGesture SettingsGesture => new(Key.OemComma, CommandModifier);

    /// <summary>Ctrl+W or ⌘W : close the window (Josour stays in the tray or the menu bar).</summary>
    public static KeyGesture CloseGesture => new(Key.W, CommandModifier);

    /// <summary>⌘Q on macOS; Windows quits from the tray.</summary>
    public static KeyGesture QuitGesture => new(Key.Q, CommandModifier);
}
