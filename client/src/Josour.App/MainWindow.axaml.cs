using Avalonia.Controls;
using Josour.App.Services;
using Josour.App.ViewModels;

namespace Josour.App;

/// <summary>Compact shell window. Closing hides it to the tray; only the tray's Exit really closes it.</summary>
public partial class MainWindow : Window
{
    private readonly IShellService _shell;

    public MainWindow(MainViewModel viewModel, IShellService shell)
    {
        _shell = shell;
        InitializeComponent();
        DataContext = viewModel;
    }

    protected override void OnClosing(WindowClosingEventArgs e)
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
