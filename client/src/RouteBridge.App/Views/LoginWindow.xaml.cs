using RouteBridge.App.ViewModels;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace RouteBridge.App.Views;

/// <summary>Sign-in window (one at a time; <see cref="Services.ShellService"/> owns the instance). Closing it leaves the app in the tray, signed out.</summary>
public partial class LoginWindow : FluentWindow
{
    public LoginWindow(LoginViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        SystemThemeWatcher.Watch(this);
    }
}
