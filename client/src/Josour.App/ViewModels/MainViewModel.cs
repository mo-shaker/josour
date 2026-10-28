using System.ComponentModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Josour.App.Services;
using Josour.App.Services.Notifications;
using Josour.Core.Control;
using Josour.Core.Session;
using Josour.Infrastructure.Api;
using Josour.Infrastructure.Device;
using Josour.Infrastructure.Session;
using Josour.Infrastructure.SystemProxy;

namespace Josour.App.ViewModels;

/// <summary>Shell state shared by MainWindow and the tray: signed-in user, connection status line, availability, Start with Windows, Sign out.</summary>
public sealed partial class MainViewModel : ObservableObject
{
    private readonly IStartupRegistration _startup;
    private readonly IShellService _shell;
    private readonly IControlChannel _controlChannel;
    private readonly ControlChannelConnector _connector;
    private readonly IAuthSession _auth;
    private readonly IAuthFlow _authFlow;
    private readonly ISystemProxyGuard _systemProxy;
    private readonly INotifier _notifier;
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
        ContactsViewModel contacts,
        SessionCoordinator sessions,
        SessionPanelViewModel session,
        IStartupRegistration startup,
        IShellService shell,
        IControlChannel controlChannel,
        ControlChannelConnector connector,
        IAuthSession auth,
        IAuthFlow authFlow,
        IDeviceInfoProvider deviceInfo,
        StartupOptions options,
        ISystemProxyGuard systemProxy,
        INotifier notifier,
        ILogger<MainViewModel> logger)
    {
        Host = host;
        Contacts = contacts;
        Sessions = sessions;
        Session = session;
        _startup = startup;
        _shell = shell;
        _controlChannel = controlChannel;
        _connector = connector;
        _auth = auth;
        _authFlow = authFlow;
        _systemProxy = systemProxy;
        _notifier = notifier;
        _logger = logger;

        // "v0.1.0" is a technical value: kept left to right even in the mirrored window.
        VersionText = UiFlow.Ltr(string.Format(UiFlow.Culture, Strings.VersionFormat, deviceInfo.AppVersion));
        IsDebugMenuVisible = options.DebugMenu;

        Host.PropertyChanged += OnHostPropertyChanged;
        _controlChannel.StateChanged += _ => UiThread.Post(UpdateStatus);
        _connector.Changed += () => UiThread.Post(UpdateStatus);
        _auth.Changed += (_, _) => UiThread.Post(UpdateStatus);
        UpdateStatus();
    }

    public HostViewModel Host { get; }

    /// <summary>The Contacts tab: people, invitations, requests and offers (ADR-0014).</summary>
    public ContactsViewModel Contacts { get; }

    /// <summary>The session lifecycle itself (phase, tunnel, system proxy); the tray and the pages ask it for state.</summary>
    public SessionCoordinator Sessions { get; }

    /// <summary>The live session panel that replaces the two tabs while a session runs.</summary>
    public SessionPanelViewModel Session { get; }

    public string VersionText { get; }

    /// <summary>Hidden debug menu (Simulate incoming request): DEBUG builds or <c>--debug</c>.</summary>
    public bool IsDebugMenuVisible { get; }

    /// <summary>
    /// Whether the admin panel is offered. It follows the signed-in account's role, so it appears on sign-in and
    /// goes away on sign-out without the window being rebuilt.
    /// <para>
    /// This is presentation only. Every admin endpoint answers 403 to a non-administrator whatever the app shows,
    /// and it must stay that way: a client is not a place to enforce a permission.
    /// </para>
    /// </summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ShowAdminCommand))]
    private bool _isAdmin;

    /// <summary>Mirror of <see cref="HostViewModel.IsAvailable"/> for the tray menu; HostViewModel owns the state.</summary>
    public bool IsAvailable
    {
        get => Host.IsAvailable;
        set => Host.IsAvailable = value;
    }

    public bool StartAtLogin
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
        IsAdmin = user is not null && string.Equals(user.Role, "admin", StringComparison.OrdinalIgnoreCase);
        UserDisplayName = user?.DisplayName ?? string.Empty;

        var connection = DescribeConnection();
        StatusText = user is null
            ? Strings.StatusNotSignedIn
            : string.Format(UiFlow.Culture, Strings.StatusSignedInFormat, user.DisplayName, connection);
        TrayTooltip = user is null
            ? Strings.TrayTooltipNotSignedIn
            : string.Format(UiFlow.Culture, Strings.TrayTooltipSignedInFormat, user.DisplayName, connection);
    }

    /// <summary>The connection half of the status line and the tray tooltip; the About window quotes the same sentence.</summary>
    private string DescribeConnection() =>
        ConnectionStatusText.Describe(_controlChannel.State, _connector.LastClose, _connector.CanConnect);

    [RelayCommand]
    private void ShowWindow() => _shell.ShowMainWindow();

    [RelayCommand]
    private void ShowSettings() => _shell.ShowSettingsWindow();

    [RelayCommand]
    private void ShowAbout() => _shell.ShowAboutWindow();

    [RelayCommand(CanExecute = nameof(IsAdmin))]
    private void ShowAdmin() => _shell.ShowAdminWindow();

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

    /// <summary>
    /// Tray → "Repair internet settings": puts back the system proxy a guest session changed. A live guest session is what
    /// the settings point at, so it is ended the normal way first; a change left by a crash is restored from its marker.
    /// </summary>
    [RelayCommand]
    private async Task RepairInternetSettingsAsync()
    {
        var hadChange = _systemProxy.IsApplied;
        bool repaired;
        try
        {
            if (Sessions.CurrentSession is { IsHost: false } && Sessions.Phase is SessionPhase.Preparing or SessionPhase.Connecting or SessionPhase.Active)
            {
                await Sessions.DisconnectAsync();
            }

            // A false from RestoreAsync is also "someone else put it back first": what counts is whether a change is still in force.
            repaired = !_systemProxy.IsApplied || await _systemProxy.RestoreAsync(CancellationToken.None) || !_systemProxy.IsApplied;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Repairing the internet settings failed");
            repaired = false;
        }

        _logger.LogInformation("Repair internet settings: change in force={HadChange} repaired={Repaired}", hadChange, repaired);
        _notifier.ShowInfo(
            Strings.TrayRepairInternet,
            !repaired ? Strings.RepairInternetFailed : hadChange ? Strings.RepairInternetDone : Strings.RepairInternetNothing);
    }

    [RelayCommand]
    private Task SimulateIncomingRequestAsync() => Host.SimulateIncomingRequestCommand.ExecuteAsync(null);
}
