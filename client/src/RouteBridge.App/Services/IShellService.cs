namespace RouteBridge.App.Services;

/// <summary>Window-level actions ViewModels may request without referencing WPF windows.</summary>
public interface IShellService
{
    /// <summary>True once the user chose Exit from the tray; MainWindow then really closes instead of hiding.</summary>
    bool IsExiting { get; }

    void ShowMainWindow();

    void HideMainWindow();

    void Exit();
}
