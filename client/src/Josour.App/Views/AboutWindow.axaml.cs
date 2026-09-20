using Avalonia.Controls;
using Josour.App.ViewModels;

namespace Josour.App.Views;

/// <summary>About / diagnostics window (one at a time; <see cref="Services.ShellService"/> owns the instance).</summary>
public partial class AboutWindow : Window
{
    private async void ShowLicenses(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        await new LicensesWindow().ShowDialog(this);
    }

    public AboutWindow(AboutViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        InitializeComponent();
        DataContext = viewModel;

        // The window can be reopened long after it was built; re-read the connection state each time it is shown.
        Activated += (_, _) => viewModel.Refresh();
    }
}
