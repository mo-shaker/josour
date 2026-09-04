namespace RouteBridge.App.Services;

/// <summary>Window-level actions ViewModels may request without referencing WPF windows.</summary>
public interface IShellService
{
    /// <summary>True once the user chose Exit from the tray; MainWindow then really closes instead of hiding.</summary>
    bool IsExiting { get; }

    /// <summary>Shows the main window when signed in; otherwise shows the sign-in window instead.</summary>
    void ShowMainWindow();

    void HideMainWindow();

    /// <summary>Shows (or brings to front) the single sign-in window.</summary>
    void ShowLoginWindow();

    void CloseLoginWindow();

    void Exit();
}
