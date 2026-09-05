using RouteBridge.App.ViewModels;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace RouteBridge.App.Views;

/// <summary>
/// The guided first run (one at a time; <see cref="Services.ShellService"/> owns the instance). Closing it before the end
/// leaves the app in the tray, signed out — the shell then offers the sign-in window like any other unsigned start.
/// </summary>
public partial class FirstRunWindow : FluentWindow
{
    public FirstRunWindow(FirstRunViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        InitializeComponent();
        DataContext = viewModel;
        viewModel.Closed += OnViewModelClosed;
        Closed += (_, _) => viewModel.Closed -= OnViewModelClosed;
        SystemThemeWatcher.Watch(this);
    }

    private void OnViewModelClosed(object? sender, EventArgs e) => Close();
}
