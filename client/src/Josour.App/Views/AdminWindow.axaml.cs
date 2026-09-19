using Avalonia.Controls;
using Josour.App.ViewModels;

namespace Josour.App.Views;

/// <summary>
/// The admin panel (one at a time; <see cref="Services.ShellService"/> owns the instance). Shown only to an
/// administrator — but the server is what enforces that, and every call in the view model survives a 403.
/// </summary>
public partial class AdminWindow : Window
{
    public AdminWindow(AdminViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        InitializeComponent();
        DataContext = viewModel;

        // The list is fetched when the window opens rather than when the view model is built, so reopening it
        // shows the accounts as they are now.
        Opened += (_, _) => viewModel.RefreshCommand.Execute(null);
    }
}
