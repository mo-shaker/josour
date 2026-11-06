using System.ComponentModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Josour.App.Services;
using Josour.Brand.Controls;
using Josour.App.Services.Notifications;
using Josour.Core.Control;
using Josour.Core.Session;
using Josour.Infrastructure.Api;
using Josour.Infrastructure.Device;
using Josour.Infrastructure.Session;
using Josour.Infrastructure.SystemProxy;

namespace Josour.App.ViewModels;

/// <summary>What the main window's status hero shows: <see cref="HostStatus"/>, or a session on top of it.</summary>
public enum HeroState
{
    Off,
    Connecting,
    Ready,
    Unreachable,
    NeedsAttention,
    HostSession,
    GuestSession,
    SessionEnded,
}

/// <summary>The tray and menu-bar icon (Phase 1 decision 7): four shapes, each also said in words in the tooltip and menu.</summary>
public enum TrayIconState
{
    Available,
    Off,
    Unreachable,
    InSession,
}

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

    /// <summary>The signed-in account's email, left to right (the "This device" tab).</summary>
    [ObservableProperty]
    private string _accountText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsHeroSession), nameof(IsHeroGuestSession), nameof(IsHeroEnded), nameof(IsHeroStatus), nameof(IsHeroProblem), nameof(CanReconnect), nameof(ShowsAvailabilityInHero))]
    private HeroState _hero;

    [ObservableProperty]
    private string _heroTitle = string.Empty;

    [ObservableProperty]
    private string _heroText = string.Empty;

    [ObservableProperty]
    private MotifState _heroMotif = MotifState.Off;

    [ObservableProperty]
    private TrayIconState _trayState = TrayIconState.Off;

    /// <summary>"Started without asking, because you allow Sara to." Empty unless the hero's session began that way.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAutoNote))]
    private string _autoNoteText = string.Empty;

    public bool HasAutoNote => AutoNoteText.Length > 0;

    /// <summary>This computer's name, left to right (the "This device" tab).</summary>
    public string DeviceNameText { get; }

    /// <summary>The state in words, the first (greyed) line of the tray menu.</summary>
    [ObservableProperty]
    private string _trayStateText = string.Empty;

    [ObservableProperty]
    private bool _isConfirmingSignOut;

    [ObservableProperty]
    private bool _isConfirmingQuit;

    [ObservableProperty]
    private string _confirmText = string.Empty;

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
        DeviceNameText = string.Format(UiFlow.Culture, "{0}: {1}", Strings.AboutDeviceNameLabel, UiFlow.Ltr(deviceInfo.DeviceName));

        Host.PropertyChanged += OnHostPropertyChanged;
        Session.PropertyChanged += (_, _) => UpdateHero();
        _controlChannel.StateChanged += _ => UiThread.Post(UpdateStatus);
        _connector.Changed += () => UiThread.Post(UpdateStatus);
        _auth.Changed += (_, _) => UiThread.Post(UpdateStatus);
        UpdateStatus();
    }

    public bool IsHeroSession => Hero is HeroState.HostSession or HeroState.GuestSession;

    public bool IsHeroGuestSession => Hero == HeroState.GuestSession;

    public bool IsHeroEnded => Hero == HeroState.SessionEnded;

    /// <summary>The hero shows sharing's status (not a session): title, text and the Available switch.</summary>
    public bool IsHeroStatus => !IsHeroSession && !IsHeroEnded;

    /// <summary>A problem state: the title carries a warning icon, so the state never rests on the arch's colour.</summary>
    public bool IsHeroProblem => Hero is HeroState.Unreachable or HeroState.NeedsAttention;

    public bool CanReconnect => Hero == HeroState.Unreachable;

    public bool ShowsAvailabilityInHero => IsHeroStatus;

    /// <summary>Quitting or signing out would stop something: sharing, or a session that is running.</summary>
    public bool WouldInterrupt => Host.IsAvailable || IsSessionLive;

    private bool IsSessionLive =>
        Sessions.CurrentSession is not null && Sessions.Phase is SessionPhase.Preparing or SessionPhase.Connecting or SessionPhase.Active;

    /// <summary>
    /// The hero's one answer (docs/design/desktop-phase2-mockups.html, "Which hero state wins"): a session first, then its
    /// summary, then sharing's own status. Only existing facts are read.
    /// </summary>
    private void UpdateHero()
    {
        var session = Sessions.CurrentSession;
        var peer = session?.PeerUserDisplayName ?? string.Empty;
        if (Session.IsVisible && !Session.IsEnded && session is not null)
        {
            Hero = session.IsHost ? HeroState.HostSession : HeroState.GuestSession;
            HeroTitle = string.Format(UiFlow.Culture, session.IsHost ? Strings.MobileServiceSessionTitleFormat : Strings.DesktopGuestSessionTitleFormat, peer);
            HeroText = Session.PhaseText;
            HeroMotif = MotifState.Session;
        }
        else if (Session.IsEnded)
        {
            Hero = HeroState.SessionEnded;
            HeroTitle = Strings.SessionPhaseEnded;
            HeroText = Session.EndedText;
            HeroMotif = MotifState.Ended;
        }
        else
        {
            (Hero, HeroTitle, HeroText, HeroMotif) = Host.Status switch
            {
                HostStatus.Off => (HeroState.Off, Strings.MobileStatusOffTitle, Strings.DesktopStatusOffText, MotifState.Off),
                HostStatus.Connecting => (HeroState.Connecting, Strings.MobileStatusConnectingTitle, Strings.MobileStatusConnectingText, MotifState.Connecting),
                HostStatus.Unreachable => (HeroState.Unreachable, Strings.MobileStatusUnreachableTitle, Strings.DesktopUnreachableText, MotifState.Unreachable),
                HostStatus.NeedsAttention => (HeroState.NeedsAttention, Strings.MobileStatusAttentionTitle, Strings.HostFirewallWarningMessage, MotifState.Attention),
                _ => (HeroState.Ready, Strings.MobileStatusReadyTitle, Strings.MobileReadyText, MotifState.Ready),
            };
        }

        AutoNoteText = Hero == HeroState.HostSession && Host.SessionStartedAutomatically
            ? string.Format(UiFlow.Culture, Strings.MobileSessionAutoNoteFormat, peer)
            : string.Empty;

        TrayState = Hero switch
        {
            HeroState.HostSession or HeroState.GuestSession => TrayIconState.InSession,
            HeroState.Off => TrayIconState.Off,
            HeroState.Unreachable => TrayIconState.Unreachable,
            _ => TrayIconState.Available,
        };
        TrayStateText = HeroTitle;
        var user = _auth.CurrentUser;
        TrayTooltip = user is null
            ? Strings.TrayTooltipNotSignedIn
            : string.Format(UiFlow.Culture, Strings.TrayTooltipSignedInFormat, HeroTitle, user.DisplayName);
        OnPropertyChanged(nameof(WouldInterrupt));
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

        if (e.PropertyName is nameof(HostViewModel.Status) or nameof(HostViewModel.IsAvailable) or nameof(HostViewModel.SessionStartedAutomatically))
        {
            UpdateHero();
        }
    }

    private void UpdateStatus()
    {
        var user = _auth.CurrentUser;
        IsSignedIn = user is not null;
        IsAdmin = user is not null && string.Equals(user.Role, "admin", StringComparison.OrdinalIgnoreCase);
        UserDisplayName = user?.DisplayName ?? string.Empty;

        AccountText = user is null ? string.Empty : UiFlow.Ltr(user.Email);
        ConnectionText = DescribeConnection();
        StatusText = user is null
            ? Strings.StatusNotSignedIn
            : string.Format(UiFlow.Culture, Strings.StatusSignedInFormat, user.DisplayName, ConnectionText);
        ServerStatusText = string.Format(UiFlow.Culture, Strings.MobileServerStatusFormat, ConnectionText);
        UpdateHero();
    }

    /// <summary>The connection to the server in words ("Connected", "Reconnecting…").</summary>
    [ObservableProperty]
    private string _connectionText = string.Empty;

    /// <summary>"Josour server: Connected", the first row of the "This device" tab.</summary>
    [ObservableProperty]
    private string _serverStatusText = string.Empty;

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

    /// <summary>Asks first, naming what stops (Phase 2: sign out is always confirmed).</summary>
    [RelayCommand(CanExecute = nameof(IsSignedIn))]
    private void AskSignOut()
    {
        IsConfirmingQuit = false;
        ConfirmText = IsSessionLive
            ? string.Format(UiFlow.Culture, Strings.DesktopConfirmEndsSessionFormat, Sessions.CurrentSession?.PeerUserDisplayName ?? string.Empty)
            : Strings.DesktopSignOutConfirmText;
        IsConfirmingSignOut = true;
        _shell.ShowMainWindow();
    }

    [RelayCommand]
    private void CancelSignOut() => IsConfirmingSignOut = false;

    [RelayCommand(CanExecute = nameof(IsSignedIn))]
    private async Task SignOutAsync()
    {
        IsConfirmingSignOut = false;
        try
        {
            await _authFlow.SignOutAsync(CancellationToken.None);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Sign out failed");
        }
    }

    /// <summary>
    /// The tray's Exit and ⌘Q: asks first while sharing or in a session (Phase 1 decision 11), quits at once otherwise.
    /// Returns whether the app is quitting now.
    /// </summary>
    public bool RequestQuit()
    {
        if (!WouldInterrupt)
        {
            _shell.Exit();
            return true;
        }

        IsConfirmingSignOut = false;
        ConfirmText = IsSessionLive
            ? string.Format(UiFlow.Culture, Strings.DesktopConfirmEndsSessionFormat, Sessions.CurrentSession?.PeerUserDisplayName ?? string.Empty)
            : Strings.DesktopQuitConfirmText;
        IsConfirmingQuit = true;
        _shell.ShowMainWindow();
        return false;
    }

    [RelayCommand]
    private void AskQuit() => RequestQuit();

    [RelayCommand]
    private void CancelQuit() => IsConfirmingQuit = false;

    [RelayCommand]
    private void Exit()
    {
        IsConfirmingQuit = false;
        _shell.Exit();
    }

    /// <summary>
    /// "Reconnect now": a channel that is backing off retries at once, one that gave up connects again (the phone's
    /// WakeAsync). The channel's own logic is unchanged; this only asks it not to wait.
    /// </summary>
    [RelayCommand]
    private async Task ReconnectNowAsync()
    {
        _logger.LogInformation("Reconnect asked from the status hero; the channel is {State}", _controlChannel.State);
        if (_controlChannel is IReconnectNow wakeable)
        {
            wakeable.ReconnectNow("the user asked");
        }

        try
        {
            if (_controlChannel.State == ControlChannelState.Disconnected)
            {
                await _connector.ConnectAsync(CancellationToken.None);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Reconnecting the control channel failed");
        }
    }

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
