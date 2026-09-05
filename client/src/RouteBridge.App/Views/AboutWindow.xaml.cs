using RouteBridge.App.ViewModels;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace RouteBridge.App.Views;

/// <summary>About / diagnostics window (one at a time; <see cref="Services.ShellService"/> owns the instance).</summary>
public partial class AboutWindow : FluentWindow
{
    public AboutWindow(AboutViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        InitializeComponent();
        DataContext = viewModel;

        // The window can be reopened long after it was built; re-read the connection state each time it is shown.
        Activated += (_, _) => viewModel.Refresh();
        SystemThemeWatcher.Watch(this);
    }
}
