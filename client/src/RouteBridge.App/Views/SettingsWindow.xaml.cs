using RouteBridge.App.ViewModels;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace RouteBridge.App.Views;

/// <summary>
/// Settings window (one at a time; <see cref="Services.ShellService"/> owns the instance). Nothing is written until Save,
/// which is also what closes it — the view model raises <see cref="SettingsViewModel.Closed"/> for both Save and Cancel.
/// </summary>
public partial class SettingsWindow : FluentWindow
{
    public SettingsWindow(SettingsViewModel viewModel)
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
