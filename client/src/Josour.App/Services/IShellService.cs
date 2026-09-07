namespace Josour.App.Services;

/// <summary>Window-level actions ViewModels may request without referencing WPF windows.</summary>
public interface IShellService
{
    /// <summary>True once the user chose Exit from the tray; MainWindow then really closes instead of hiding.</summary>
    bool IsExiting { get; }

    /// <summary>Shows the main window when signed in; otherwise shows the sign-in window instead.</summary>
    void ShowMainWindow();

    void HideMainWindow();

    /// <summary>
    /// Shows (or brings to front) the sign-in surface: the sign-in window, or the guided first run when no usable server
    /// address is configured — an app that cannot name its server has nothing to sign in to.
    /// </summary>
    void ShowLoginWindow();

    void CloseLoginWindow();

    /// <summary>Shows (or brings to front) the guided first run: server address → sign in → readiness.</summary>
    void ShowFirstRunWindow();

    /// <summary>Shows (or brings to front) the settings window.</summary>
    void ShowSettingsWindow();

    /// <summary>Shows (or brings to front) the About / diagnostics window.</summary>
    void ShowAboutWindow();

    /// <summary>
    /// Opens a folder in the file manager (the About window's "open the log folder"). False with a reason rather than an
    /// exception: a failure here is a message in the window, never a crash.
    /// </summary>
    bool TryOpenFolder(string path, out string? error);

    void Exit();
}
