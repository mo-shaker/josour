using Avalonia.Controls;
using Josour.App.ViewModels;

namespace Josour.App.Views;

/// <summary>Sign-in window (one at a time; <see cref="Services.ShellService"/> owns the instance). Closing it leaves the app in the tray, signed out.</summary>
public partial class LoginWindow : Window
{
    public LoginWindow(LoginViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
