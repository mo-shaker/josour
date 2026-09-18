using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Media;
using Avalonia.Platform;
using Microsoft.Extensions.Logging;
using Josour.App.ViewModels;

namespace Josour.App.Services;

/// <summary>
/// The notification-area icon (Avalonia's <see cref="TrayIcon"/>, which is the Windows tray and the macOS menu bar).
/// Menu items bind straight to <see cref="MainViewModel"/> so the tray, the Host page toggle and the settings share
/// one state. Must be initialised on the UI thread.
/// </summary>
public sealed class TrayService : IDisposable
{
    private const string IconUri = "avares://Josour/Assets/josour.ico";

    private readonly MainViewModel _viewModel;
    private readonly ILogger<TrayService> _logger;
    private TrayIcon? _icon;
    private TrayIcons? _icons;

    public TrayService(MainViewModel viewModel, ILogger<TrayService> logger)
    {
        _viewModel = viewModel;
        _logger = logger;
    }

    public void Initialize()
    {
        if (_icon is not null)
        {
            return;
        }

        var application = Application.Current;
        if (application is null)
        {
            _logger.LogWarning("No application instance; the tray icon was not created");
            return;
        }

        _icon = new TrayIcon
        {
            ToolTipText = Strings.TrayTooltip,
            Icon = LoadIcon(),
            Menu = BuildMenu(),
            IsVisible = true,
        };

        // Avalonia raises Clicked for a single click; the double-click the WPF build used has no equivalent, and a
        // single click is what a menu-bar item is expected to answer to on macOS anyway.
        _icon.Clicked += (_, _) => _viewModel.ShowWindowCommand.Execute(null);
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;

        _icons = new TrayIcons { _icon };
        TrayIcon.SetIcons(application, _icons);
        _logger.LogInformation("Tray icon created");
    }

    /// <summary>
    /// The tooltip is not a bindable target on <see cref="TrayIcon"/> (it is not in a visual tree), so the one
    /// property that changes while the app runs is pushed by hand.
    /// </summary>
    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.TrayTooltip) && _icon is not null)
        {
            UiThread.Post(() => _icon.ToolTipText = _viewModel.TrayTooltip);
        }
    }

    private NativeMenu BuildMenu()
    {
        // A tray menu is its own visual root: it inherits neither the DataContext nor the window's flow direction,
        // so both are set here or the tray would be the one part of the app left unmirrored in Arabic.
        var menu = new NativeMenu();

        menu.Add(Item(Strings.TrayShowWindow, _viewModel.ShowWindowCommand));
        menu.Add(new NativeMenuItemSeparator());
        menu.Add(Toggle(Strings.TrayAvailableForRequests, nameof(MainViewModel.IsAvailable)));
        menu.Add(Toggle(Strings.TrayStartAtLogin, nameof(MainViewModel.StartAtLogin)));
        menu.Add(new NativeMenuItemSeparator());
        menu.Add(Item(Strings.TraySettings, _viewModel.ShowSettingsCommand));
        menu.Add(Item(Strings.TrayAbout, _viewModel.ShowAboutCommand));

        if (_viewModel.IsDebugMenuVisible)
        {
            var debug = new NativeMenuItem(Strings.TrayDebug)
            {
                Menu = new NativeMenu { Item(Strings.TraySimulateIncomingRequest, _viewModel.SimulateIncomingRequestCommand) },
            };
            menu.Add(new NativeMenuItemSeparator());
            menu.Add(debug);
        }

        menu.Add(new NativeMenuItemSeparator());
        menu.Add(Item(Strings.TraySignOut, _viewModel.SignOutCommand));
        menu.Add(Item(Strings.TrayExit, _viewModel.ExitCommand));
        return menu;
    }

    private static NativeMenuItem Item(string header, System.Windows.Input.ICommand command) =>
        new(header) { Command = command };

    /// <summary>A checkable item bound two-way, so ticking it in the tray is the same act as toggling it on the page.</summary>
    private NativeMenuItem Toggle(string header, string propertyPath)
    {
        var item = new NativeMenuItem(header) { ToggleType = NativeMenuItemToggleType.CheckBox };
        item.Bind(
            NativeMenuItem.IsCheckedProperty,
            new Binding(propertyPath) { Source = _viewModel, Mode = BindingMode.TwoWay });
        return item;
    }

    private static WindowIcon LoadIcon() => new(AssetLoader.Open(new Uri(IconUri)));

    public void Dispose()
    {
        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        if (_icon is not null)
        {
            _icon.IsVisible = false;
            _icon.Dispose();
            _icon = null;
        }

        _icons = null;
    }
}
