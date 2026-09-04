using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media.Imaging;
using H.NotifyIcon;
using Microsoft.Extensions.Logging;
using RouteBridge.App.ViewModels;

namespace RouteBridge.App.Services;

/// <summary>
/// Notification-area icon (H.NotifyIcon.Wpf). Menu items bind straight to <see cref="MainViewModel"/> so the tray,
/// the Host page toggle and the settings share one state. Must be initialised on the UI thread.
/// </summary>
public sealed class TrayService : IDisposable
{
    private const string IconUri = "pack://application:,,,/Assets/routebridge.ico";

    private readonly MainViewModel _viewModel;
    private readonly ILogger<TrayService> _logger;
    private TaskbarIcon? _icon;

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

        _icon = new TaskbarIcon
        {
            ToolTipText = Strings.TrayTooltip,
            IconSource = LoadIcon(),
            ContextMenu = BuildMenu(),
            DataContext = _viewModel,
            DoubleClickCommand = _viewModel.ShowWindowCommand,
            NoLeftClickDelay = true,
        };
        _icon.SetBinding(TaskbarIcon.ToolTipTextProperty, new Binding(nameof(MainViewModel.TrayTooltip)));

        // Efficiency mode (EcoQoS) would throttle the tunnel while the window is hidden, so keep it off.
        _icon.ForceCreate(enablesEfficiencyMode: false);
        _logger.LogInformation("Tray icon created");
    }

    private ContextMenu BuildMenu()
    {
        // A ContextMenu is not part of the visual tree, so it does not inherit DataContext: set it explicitly.
        var menu = new ContextMenu { DataContext = _viewModel };

        menu.Items.Add(new MenuItem
        {
            Header = Strings.TrayShowWindow,
            Command = _viewModel.ShowWindowCommand,
            FontWeight = FontWeights.SemiBold,
        });
        menu.Items.Add(new Separator());
        menu.Items.Add(CheckableItem(Strings.TrayAvailableForRequests, nameof(MainViewModel.IsAvailable)));
        menu.Items.Add(CheckableItem(Strings.TrayStartWithWindows, nameof(MainViewModel.StartWithWindows)));

        if (_viewModel.IsDebugMenuVisible)
        {
            var debug = new MenuItem { Header = Strings.TrayDebug };
            debug.Items.Add(new MenuItem
            {
                Header = Strings.TraySimulateIncomingRequest,
                Command = _viewModel.SimulateIncomingRequestCommand,
            });
            menu.Items.Add(new Separator());
            menu.Items.Add(debug);
        }

        menu.Items.Add(new Separator());
        menu.Items.Add(new MenuItem { Header = Strings.TraySignOut, Command = _viewModel.SignOutCommand });
        menu.Items.Add(new MenuItem { Header = Strings.TrayExit, Command = _viewModel.ExitCommand });
        return menu;
    }

    private static MenuItem CheckableItem(string header, string propertyPath)
    {
        var item = new MenuItem { Header = header, IsCheckable = true };
        item.SetBinding(MenuItem.IsCheckedProperty, new Binding(propertyPath) { Mode = BindingMode.TwoWay });
        return item;
    }

    private static BitmapImage LoadIcon()
    {
        var image = new BitmapImage();
        image.BeginInit();
        image.UriSource = new Uri(IconUri, UriKind.Absolute);
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.EndInit();
        image.Freeze();
        return image;
    }

    public void Dispose()
    {
        _icon?.Dispose();
        _icon = null;
    }
}
