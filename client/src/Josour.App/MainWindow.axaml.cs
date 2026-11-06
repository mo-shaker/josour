using Avalonia.Controls;
using Avalonia.Input;
using CommunityToolkit.Mvvm.Input;
using Josour.App.Services;
using Josour.App.ViewModels;

namespace Josour.App;

/// <summary>Compact shell window. Closing hides it to the tray; only the tray's Exit (or ⌘Q) really closes it.</summary>
public partial class MainWindow : Window
{
    private readonly IShellService _shell;

    public MainWindow(MainViewModel viewModel, IShellService shell)
    {
        _shell = shell;
        InitializeComponent();
        DataContext = viewModel;

        // Each system's shortcuts (Phase 1 decision 11): Ctrl/⌘ , for Settings, Ctrl/⌘ W to close to the tray or menu
        // bar, ⌘Q on macOS to quit (asking first while sharing).
        if (viewModel is not null)
        {
            KeyBindings.Add(new KeyBinding { Gesture = PlatformUi.SettingsGesture, Command = viewModel.ShowSettingsCommand });
            KeyBindings.Add(new KeyBinding { Gesture = PlatformUi.CloseGesture, Command = new RelayCommand(Close) });
            if (PlatformUi.IsMac)
            {
                KeyBindings.Add(new KeyBinding { Gesture = PlatformUi.QuitGesture, Command = viewModel.AskQuitCommand });
            }
        }
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (_shell is { IsExiting: false })
        {
            e.Cancel = true;
            Hide();
            return;
        }

        base.OnClosing(e);
    }
}
