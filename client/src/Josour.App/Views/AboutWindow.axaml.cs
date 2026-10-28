using Avalonia.Controls;
using Josour.App.ViewModels;

namespace Josour.App.Views;

/// <summary>About / diagnostics window (one at a time; <see cref="Services.ShellService"/> owns the instance).</summary>
public partial class AboutWindow : Window
{
    // async void because it is an event handler, so nothing may escape it: an exception here would end the process.
    private async void ShowLicenses(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        try
        {
            await new LicensesWindow().ShowDialog(this);
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "The licences window could not be shown");
        }
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
