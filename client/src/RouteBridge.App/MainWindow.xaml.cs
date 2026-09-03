using System.ComponentModel;
using RouteBridge.App.Services;
using RouteBridge.App.ViewModels;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace RouteBridge.App;

/// <summary>Compact shell window. Closing hides it to the tray; only the tray's Exit really closes it.</summary>
public partial class MainWindow : FluentWindow
{
    private readonly IShellService _shell;

    public MainWindow(MainViewModel viewModel, IShellService shell)
    {
        _shell = shell;
        InitializeComponent();
        DataContext = viewModel;
        SystemThemeWatcher.Watch(this); // follow the Windows light/dark setting
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_shell.IsExiting)
        {
            e.Cancel = true;
            Hide();
            return;
        }

        base.OnClosing(e);
    }
}
