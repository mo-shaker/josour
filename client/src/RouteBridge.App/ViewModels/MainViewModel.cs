using System.ComponentModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using RouteBridge.App.Services;
using RouteBridge.Core.Control;
using RouteBridge.Infrastructure.Device;

namespace RouteBridge.App.ViewModels;

/// <summary>Shell state shared by MainWindow and the tray: availability, connection status line, Start with Windows.</summary>
public sealed partial class MainViewModel : ObservableObject
{
    private readonly IStartupRegistration _startup;
    private readonly IShellService _shell;
    private readonly IControlChannel _controlChannel;
    private readonly ILogger<MainViewModel> _logger;

    [ObservableProperty]
    private string _statusText = Strings.StatusOffline;

    public MainViewModel(
        HostViewModel host,
        GuestViewModel guest,
        IStartupRegistration startup,
        IShellService shell,
        IControlChannel controlChannel,
        IDeviceInfoProvider deviceInfo,
        StartupOptions options,
        ILogger<MainViewModel> logger)
    {
        Host = host;
        Guest = guest;
        _startup = startup;
        _shell = shell;
        _controlChannel = controlChannel;
        _logger = logger;

        VersionText = string.Format(CultureInfo.CurrentCulture, Strings.VersionFormat, deviceInfo.AppVersion);
        IsDebugMenuVisible = options.DebugMenu;

        Host.PropertyChanged += OnHostPropertyChanged;
        _controlChannel.StateChanged += UpdateStatus;
        UpdateStatus(_controlChannel.State);
    }

    public HostViewModel Host { get; }

    public GuestViewModel Guest { get; }

    public string VersionText { get; }

    /// <summary>Hidden debug menu (Simulate incoming request): DEBUG builds or <c>--debug</c>.</summary>
    public bool IsDebugMenuVisible { get; }

    /// <summary>Mirror of <see cref="HostViewModel.IsAvailable"/> for the tray menu; HostViewModel owns the state.</summary>
    public bool IsAvailable
    {
        get => Host.IsAvailable;
        set => Host.IsAvailable = value;
    }

    public bool StartWithWindows
    {
        get => _startup.IsEnabled;
        set
        {
            if (_startup.IsEnabled == value)
            {
                return;
            }

            try
            {
                _startup.SetEnabled(value);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _logger.LogError(ex, "Could not update Start with Windows");
            }

            OnPropertyChanged(); // re-read the registry so the checkbox reflects reality even if the write failed
        }
    }

    private void OnHostPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(HostViewModel.IsAvailable))
        {
            OnPropertyChanged(nameof(IsAvailable));
        }
    }

    private void UpdateStatus(ControlChannelState state) => StatusText = state switch
    {
        ControlChannelState.Connected => Strings.StatusConnected,
        ControlChannelState.Connecting => Strings.StatusConnecting,
        ControlChannelState.Reconnecting => Strings.StatusReconnecting,
        _ => Strings.StatusOffline,
    };

    // WEEK 2: MainViewModel.SignInAsync — LoginWindow + ApiClient.LoginAsync (ISecretStore keeps refresh token + device secret),
    // then IControlChannel.ConnectAsync(accessToken, deviceId, diagnostics, ct) and route MessageReceived to Host/Guest.

    [RelayCommand]
    private void ShowWindow() => _shell.ShowMainWindow();

    [RelayCommand]
    private void Exit() => _shell.Exit();

    [RelayCommand]
    private Task SimulateIncomingRequestAsync() => Host.SimulateIncomingRequestCommand.ExecuteAsync(null);
}
