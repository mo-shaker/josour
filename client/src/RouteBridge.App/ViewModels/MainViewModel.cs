using System.ComponentModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using RouteBridge.App.Services;
using RouteBridge.Core.Control;
using RouteBridge.Infrastructure.Api;
using RouteBridge.Infrastructure.Device;

namespace RouteBridge.App.ViewModels;

/// <summary>Shell state shared by MainWindow and the tray: signed-in user, connection status line, availability, Start with Windows, Sign out.</summary>
public sealed partial class MainViewModel : ObservableObject
{
    private readonly IStartupRegistration _startup;
    private readonly IShellService _shell;
    private readonly IControlChannel _controlChannel;
    private readonly IAuthSession _auth;
    private readonly IAuthFlow _authFlow;
    private readonly ILogger<MainViewModel> _logger;

    [ObservableProperty]
    private string _statusText = Strings.StatusNotSignedIn;

    [ObservableProperty]
    private string _trayTooltip = Strings.TrayTooltipNotSignedIn;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SignOutCommand))]
    private bool _isSignedIn;

    [ObservableProperty]
    private string _userDisplayName = string.Empty;

    public MainViewModel(
        HostViewModel host,
        GuestViewModel guest,
        SessionCoordinator sessions,
        IStartupRegistration startup,
        IShellService shell,
        IControlChannel controlChannel,
        IAuthSession auth,
        IAuthFlow authFlow,
        IDeviceInfoProvider deviceInfo,
        StartupOptions options,
        ILogger<MainViewModel> logger)
    {
        Host = host;
        Guest = guest;
        Sessions = sessions;
        _startup = startup;
        _shell = shell;
        _controlChannel = controlChannel;
        _auth = auth;
        _authFlow = authFlow;
        _logger = logger;

        VersionText = string.Format(CultureInfo.CurrentCulture, Strings.VersionFormat, deviceInfo.AppVersion);
        IsDebugMenuVisible = options.DebugMenu;

        Host.PropertyChanged += OnHostPropertyChanged;
        _controlChannel.StateChanged += _ => UiThread.Post(UpdateStatus);
        _auth.Changed += (_, _) => UiThread.Post(UpdateStatus);
        UpdateStatus();
    }

    public HostViewModel Host { get; }

    public GuestViewModel Guest { get; }

    /// <summary>Session phase + current session for the SessionPanel (WEEK 4).</summary>
    public SessionCoordinator Sessions { get; }

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

    private void UpdateStatus()
    {
        var user = _auth.CurrentUser;
        IsSignedIn = user is not null;
        UserDisplayName = user?.DisplayName ?? string.Empty;

        var connection = _controlChannel.State switch
        {
            ControlChannelState.Connected => Strings.StatusConnected,
            ControlChannelState.Connecting => Strings.StatusConnecting,
            ControlChannelState.Reconnecting => Strings.StatusReconnecting,
            _ => Strings.StatusOffline,
        };

        StatusText = user is null
            ? Strings.StatusNotSignedIn
            : string.Format(CultureInfo.CurrentCulture, Strings.StatusSignedInFormat, user.DisplayName, connection);
        TrayTooltip = user is null
            ? Strings.TrayTooltipNotSignedIn
            : string.Format(CultureInfo.CurrentCulture, Strings.TrayTooltipSignedInFormat, user.DisplayName);
    }

    [RelayCommand]
    private void ShowWindow() => _shell.ShowMainWindow();

    [RelayCommand(CanExecute = nameof(IsSignedIn))]
    private async Task SignOutAsync()
    {
        try
        {
            await _authFlow.SignOutAsync(CancellationToken.None);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Sign out failed");
        }
    }

    [RelayCommand]
    private void Exit() => _shell.Exit();

    [RelayCommand]
    private Task SimulateIncomingRequestAsync() => Host.SimulateIncomingRequestCommand.ExecuteAsync(null);
}
