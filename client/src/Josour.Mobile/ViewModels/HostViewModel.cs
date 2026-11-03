using System.ComponentModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Josour.Core.Control;
using Josour.Core.Session;
using Josour.Core.Tunnel;
using Josour.Infrastructure.Api;
using Josour.Infrastructure.Contacts;
using Josour.Infrastructure.Localization;
using Josour.Infrastructure.Session;
using Josour.Mobile.Platform;
using Josour.Mobile.Services;
using Microsoft.Extensions.Logging;

namespace Josour.Mobile.ViewModels;

/// <summary>
/// The phone as a host: the "Available" switch (<c>host.available</c>, confirmed by <c>host.state</c>), the incoming request
/// (<c>request.incoming</c> → consent screen or automatic acceptance for a trusted contact → <c>request.accept</c> /
/// <c>request.reject</c>), and the live session the <see cref="SessionCoordinator"/> runs. While available or in a session the
/// foreground service keeps the process, and so the control channel and the tunnel, alive with the screen off.
/// <para>
/// The phone's network is whatever Android's default network is — Wi-Fi or mobile data. The host only ever connects
/// outwards (to the server, the relay and the sites the guest asks for), so neither carrier-grade NAT nor a Wi-Fi router in
/// front of the phone stands in the way; the public address the sites see is the one the server reports in <c>hello.ack</c>.
/// </para>
/// </summary>
public sealed partial class HostViewModel : ObservableObject, IDisposable
{
    private readonly IControlChannel _channel;
    private readonly SessionCoordinator _sessions;
    private readonly MobileChannelConnector _connector;
    private readonly IAllowlistDisclosure _allowlist;
    private readonly IContactsStore _contacts;
    private readonly IApiClient _api;
    private readonly IAuthSession _auth;
    private readonly MobilePlatform _platform;
    private readonly HostAvailabilityFlag _flag;
    private readonly ILogger<HostViewModel> _logger;
    private bool _suppressPublish;
    private RequestIncomingMessage? _autoAccepted;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsReady))]
    [NotifyPropertyChangedFor(nameof(IsUnreachable))]
    private bool _isAvailable;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsReady))]
    [NotifyPropertyChangedFor(nameof(IsUnreachable))]
    private bool _isAnnounced;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsReady))]
    [NotifyPropertyChangedFor(nameof(IsUnreachable))]
    [NotifyPropertyChangedFor(nameof(IsDisconnected))]
    private bool _isConnected;

    [ObservableProperty]
    private string _statusText = string.Empty;

    [ObservableProperty]
    private string _connectionText = string.Empty;

    [ObservableProperty]
    private string _networkText = string.Empty;

    [ObservableProperty]
    private string _publicIpText = string.Empty;

    [ObservableProperty]
    private bool _isCellular;

    [ObservableProperty]
    private bool _isVpn;

    [ObservableProperty]
    private bool _showBatteryHint;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowNotificationsOffWarning))]
    private bool _showNotificationsHint;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasIncoming))]
    private IncomingRequestViewModel? _incoming;

    // ---- the session ----
    [ObservableProperty]
    private bool _hasSession;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(EndSessionCommand))]
    private bool _canEndSession;

    [ObservableProperty]
    private bool _isSessionEnded;

    [ObservableProperty]
    private string _sessionGuestText = string.Empty;

    [ObservableProperty]
    private string _sessionPhaseText = string.Empty;

    [ObservableProperty]
    private string _sessionTimeText = string.Empty;

    [ObservableProperty]
    private string _sessionDataText = string.Empty;

    [ObservableProperty]
    private string _sessionEndedText = string.Empty;

    public HostViewModel(
        IControlChannel channel,
        SessionCoordinator sessions,
        MobileChannelConnector connector,
        IAllowlistDisclosure allowlist,
        IContactsStore contacts,
        IApiClient api,
        IAuthSession auth,
        MobilePlatform platform,
        HostAvailabilityFlag flag,
        ILogger<HostViewModel> logger)
    {
        _channel = channel;
        _sessions = sessions;
        _connector = connector;
        _allowlist = allowlist;
        _contacts = contacts;
        _api = api;
        _auth = auth;
        _platform = platform;
        _flag = flag;
        _logger = logger;

        _channel.StateChanged += OnChannelStateChanged;
        _channel.MessageReceived += OnMessage;
        _sessions.PropertyChanged += OnSessionChanged;
        _connector.Changed += OnConnectorChanged;
        _platform.Network.Changed += OnNetworkChanged;
        _platform.Permissions.Changed += OnPermissionsChanged;

        IsConnected = _channel.State == ControlChannelState.Connected;
        UpdateStatusText();
        UpdateConnectionText();
        ApplyNetwork(_platform.Network.Current);
        ApplyPermissions();
        RefreshSession();
    }

    public bool HasIncoming => Incoming is not null;

    /// <summary>Available, connected and confirmed by the server: a request can reach this phone right now.</summary>
    public bool IsReady => IsAvailable && IsConnected && IsAnnounced;

    /// <summary>The user wants requests, but the phone has no working connection to the server: say so, offer a retry.</summary>
    public bool IsUnreachable => IsAvailable && !IsConnected;

    /// <summary>Not connected to the server, whether or not available; the connection card offers a retry.</summary>
    public bool IsDisconnected => !IsConnected;

    /// <summary>Available, but a request arriving with the app in the background would show nothing.</summary>
    public bool ShowNotificationsOffWarning => IsAvailable && ShowNotificationsHint;

    public string SignedInText => Tr.Format(UiStringKeys.MobileSignedInAsFormat, Tr.Ltr(_auth.CurrentUser?.Email ?? string.Empty));

    /// <summary>Every text this page computes, again in the language just switched to; a failed announcement reads as "announcing".</summary>
    public void RefreshTexts()
    {
        OnPropertyChanged(nameof(SignedInText));
        UpdateStatusText();
        UpdateConnectionText();
        ApplyNetwork(_platform.Network.Current);
        RefreshSession(); // also the foreground notification
    }

    /// <summary>After sign-in or a restored start: come back available if the user left the phone that way.</summary>
    public void RestoreAvailability()
    {
        OnPropertyChanged(nameof(SignedInText));
        if (_flag.Read() && !IsAvailable)
        {
            _logger.LogInformation("Restoring availability the user left on");
            IsAvailable = true;
        }
    }

    /// <summary>Sign-out: stop being available without forgetting the choice in the flag (a new sign-in asks again).</summary>
    public void StopForSignOut()
    {
        _suppressPublish = true;
        try
        {
            IsAvailable = false;
        }
        finally
        {
            _suppressPublish = false;
        }

        _flag.Write(false);
        Incoming?.Dispose();
        Incoming = null;
        UpdateForegroundService();
    }

    /// <summary>
    /// "Reconnect now", and the app coming to the front: a channel that believes it is connected pings at once (a socket that
    /// died quietly is replaced instead of waiting for the next heart-beat), one that is backing off retries now, and one
    /// that gave up connects again. A connected, available phone also announces itself again, so the server's view of it
    /// is the phone's own.
    /// </summary>
    public async Task WakeAsync(string reason)
    {
        _logger.LogInformation("Waking the control channel ({Reason}); it is {State}", reason, _channel.State);
        if (_channel is IReconnectNow wakeable)
        {
            wakeable.ReconnectNow(reason);
        }

        try
        {
            if (_channel.State == ControlChannelState.Disconnected)
            {
                await _connector.ConnectAsync(CancellationToken.None);
            }
            else if (_channel.State == ControlChannelState.Connected && IsAvailable)
            {
                await PublishAvailabilityAsync(true);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Reconnecting the control channel failed");
        }
    }

    [RelayCommand]
    private Task ReconnectNowAsync() => WakeAsync("the user asked");

    partial void OnIsAvailableChanged(bool value)
    {
        if (!value)
        {
            IsAnnounced = false;
        }

        UpdateStatusText();
        if (_suppressPublish)
        {
            return;
        }

        _flag.Write(value);
        _logger.LogInformation("Host availability set to {Available} (control channel {State})", value, _channel.State);
        UpdateForegroundService();
        _ = PublishAvailabilityAsync(value);
    }

    partial void OnIsAnnouncedChanged(bool value) => UpdateStatusText();

    [RelayCommand]
    private void AllowNotifications() => _platform.Permissions.RequestNotificationPermission();

    [RelayCommand]
    private void AllowBackground() => _platform.Permissions.RequestIgnoreBatteryOptimizations();

    private async Task PublishAvailabilityAsync(bool available)
    {
        if (_channel.State != ControlChannelState.Connected)
        {
            _logger.LogInformation("host.available={Available} deferred until the control channel is connected", available);
            return;
        }

        try
        {
            // No listen_port: the tunnel listener only exists during a session (docs/protocol.md section 2).
            await _channel.SendAsync(new HostAvailableMessage(available, ListenPort: null), CancellationToken.None);
            _logger.LogInformation("host.available={Available} sent", available);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Could not send host.available={Available}", available);
            Dispatcher.UIThread.Post(() =>
            {
                _suppressPublish = true;
                try
                {
                    IsAvailable = false;
                }
                finally
                {
                    _suppressPublish = false;
                }

                UpdateForegroundService();
                StatusText = Tr.Get(UiStringKeys.HostStatusAvailabilityFailed);
            });
        }
    }

    private void UpdateStatusText() => StatusText = (IsAvailable, IsAnnounced) switch
    {
        (false, _) => Tr.Get(UiStringKeys.HostStatusNotAvailable),
        (true, true) => Tr.Get(UiStringKeys.HostStatusAvailable),
        (true, false) => Tr.Get(UiStringKeys.HostStatusAnnouncing),
    };

    private void UpdateConnectionText()
    {
        ConnectionText = _channel.State switch
        {
            ControlChannelState.Connected => Tr.Get(UiStringKeys.StatusConnected),
            ControlChannelState.Connecting => Tr.Get(UiStringKeys.StatusConnecting),
            ControlChannelState.Reconnecting => Tr.Get(UiStringKeys.StatusReconnecting),
            _ => Tr.Get(UiStringKeys.StatusOffline),
        };

        var ip = _connector.LastHello?.PublicIp;
        PublicIpText = string.IsNullOrEmpty(ip) ? string.Empty : Tr.Format(UiStringKeys.MobilePublicIpFormat, Tr.Ltr(ip));
    }

    private void OnConnectorChanged() => Dispatcher.UIThread.Post(UpdateConnectionText);

    private void OnChannelStateChanged(ControlChannelState state) => Dispatcher.UIThread.Post(() =>
    {
        IsConnected = state == ControlChannelState.Connected;
        UpdateConnectionText();
        if (state != ControlChannelState.Connected)
        {
            IsAnnounced = false; // the server forgets our presence the moment the socket drops
            return;
        }

        if (IsAvailable)
        {
            _ = PublishAvailabilityAsync(true);
        }
    });

    private void OnNetworkChanged(NetworkSnapshot snapshot) => Dispatcher.UIThread.Post(() => ApplyNetwork(snapshot));

    private void ApplyNetwork(NetworkSnapshot snapshot)
    {
        NetworkText = snapshot.Kind switch
        {
            NetworkKind.WiFi => Tr.Get(UiStringKeys.MobileNetworkWifi),
            NetworkKind.Cellular => Tr.Get(UiStringKeys.MobileNetworkCellular),
            NetworkKind.Ethernet => Tr.Get(UiStringKeys.MobileNetworkEthernet),
            NetworkKind.Other => Tr.Get(UiStringKeys.MobileNetworkOther),
            _ => Tr.Get(UiStringKeys.MobileNetworkNone),
        };
        IsCellular = snapshot.Kind == NetworkKind.Cellular;
        IsVpn = snapshot.IsVpn;
    }

    private void OnPermissionsChanged() => Dispatcher.UIThread.Post(ApplyPermissions);

    private void ApplyPermissions()
    {
        ShowNotificationsHint = _platform.Permissions.NeedsNotificationPermission;
        ShowBatteryHint = _platform.Permissions.IsBatteryOptimized;
    }

    private void OnMessage(ControlMessage message)
    {
        switch (message)
        {
            case HostStateMessage state:
                Dispatcher.UIThread.Post(() => IsAnnounced = IsAvailable && state.Available);
                break;

            case HostsMessage hosts when hosts.Hosts.Any(h => h.DeviceId == _auth.DeviceId):
                Dispatcher.UIThread.Post(() => IsAnnounced = IsAvailable);
                break;

            case ErrorMessage { Code: ControlErrorCodes.TrustRevoked } when _autoAccepted is { } withdrawn:
                _autoAccepted = null;
                Dispatcher.UIThread.Post(() => _ = ReceiveAsync(withdrawn, allowAuto: false));
                break;

            case RequestIncomingMessage incoming:
                Dispatcher.UIThread.Post(() => _ = ReceiveAsync(incoming, allowAuto: true));
                break;

            case RequestExpiredMessage expired:
                Dispatcher.UIThread.Post(() =>
                {
                    _platform.Notifier.CancelIncomingRequest(expired.RequestId);
                    if (Incoming?.RequestId == expired.RequestId) Incoming.Expire();
                });
                break;
        }
    }

    /// <summary>One <c>request.incoming</c>, on the UI thread.</summary>
    internal async Task ReceiveAsync(RequestIncomingMessage request, bool allowAuto)
    {
        if (!_sessions.IsAwaitingAnswer(request.RequestId))
        {
            _logger.LogWarning("Not presenting request {RequestId}: this phone is busy with a session or another request", request.RequestId);
            return;
        }

        if (Incoming?.RequestId == request.RequestId)
        {
            // The server rings a host again after it reconnects; the question is already on screen.
            _logger.LogInformation("Request {RequestId} arrived again after a reconnect; it is already on screen", request.RequestId);
            return;
        }

        if (Incoming is not null)
        {
            _logger.LogWarning("Ignoring request {RequestId}: another request is already on screen", request.RequestId);
            return;
        }

        if (allowAuto && await TryAutoAcceptAsync(request))
        {
            return;
        }

        if (!allowAuto)
        {
            _platform.Notifier.ShowInfo(Tr.Get(UiStringKeys.AutoAcceptToastTitle), Tr.Get(UiStringKeys.ErrorTrustRevoked));
        }

        var allowlist = _sessions.EnforceAllowlist
            ? await _allowlist.DescribeAsync(request.AllowlistVersion, CancellationToken.None)
            : AllowlistDisclosure.NoRestriction(request.AllowlistVersion);

        var prompt = new IncomingRequestViewModel(request, allowlist, _sessions.ServerTimeNow);
        Incoming = prompt;
        _platform.Notifier.ShowIncomingRequest(
            request.RequestId,
            Tr.Get(UiStringKeys.ToastIncomingRequestTitle),
            Tr.Format(UiStringKeys.ToastIncomingRequestBodyFormat, request.GuestName, Tr.Ltr(request.GuestDevice), request.DurationMin));

        RequestAnswer answer;
        try
        {
            answer = await prompt.Completion;
        }
        finally
        {
            _platform.Notifier.CancelIncomingRequest(request.RequestId);
            if (ReferenceEquals(Incoming, prompt)) Incoming = null;
            prompt.Dispose();
        }

        _logger.LogInformation("Request {RequestId} from {GuestName}: {Decision}", request.RequestId, request.GuestName, answer.Decision);
        switch (answer.Decision)
        {
            case RequestDecision.Accepted:
                await AnswerAsync(() => _sessions.AcceptRequestAsync(request.RequestId, CancellationToken.None), "request.accept");
                if (answer.AlwaysAllow)
                {
                    await GrantTrustAsync(request);
                }

                break;
            case RequestDecision.Rejected:
                await AnswerAsync(() => _sessions.RejectRequestAsync(request.RequestId, CancellationToken.None), "request.reject");
                break;
        }
    }

    /// <summary>
    /// ADR-0014: a contact the host already trusts is accepted without asking, when the server marked the request trusted
    /// and this phone's own copy of the contact list agrees. The host is told, never asked.
    /// </summary>
    private async Task<bool> TryAutoAcceptAsync(RequestIncomingMessage request)
    {
        if (!request.Trusted || request.GuestUserId == Guid.Empty || !_contacts.Trusts(request.GuestUserId))
        {
            return false;
        }

        _autoAccepted = request;
        var sent = false;
        await AnswerAsync(
            async () =>
            {
                await _sessions.AcceptRequestAsync(request.RequestId, auto: true, CancellationToken.None).ConfigureAwait(false);
                sent = true;
            },
            "request.accept (auto)");
        if (!sent)
        {
            _autoAccepted = null;
            return false;
        }

        _platform.Notifier.ShowInfo(
            Tr.Get(UiStringKeys.AutoAcceptToastTitle),
            Tr.Format(UiStringKeys.AutoAcceptToastBodyFormat, request.GuestName, Tr.Ltr(request.GuestDevice)));
        return true;
    }

    private async Task GrantTrustAsync(RequestIncomingMessage request)
    {
        try
        {
            await _api.SetTrustAsync(request.GuestUserId, autoAccept: true, CancellationToken.None).ConfigureAwait(false);
            await _contacts.RefreshAsync(CancellationToken.None).ConfigureAwait(false);
            _logger.LogInformation("{GuestName} may now use this connection without asking", request.GuestName);
        }
        catch (Exception ex) when (ex is ApiException or ApiUnavailableException)
        {
            _logger.LogError(ex, "Could not store the trust for {GuestName}", request.GuestName);
        }
    }

    private async Task AnswerAsync(Func<Task> send, string what)
    {
        try
        {
            await send();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Could not send {Message}", what);
        }
    }

    // ---- the session ----

    private void OnSessionChanged(object? sender, PropertyChangedEventArgs e) => Dispatcher.UIThread.Post(RefreshSession);

    private void RefreshSession()
    {
        var phase = _sessions.Phase;
        var session = _sessions.CurrentSession;
        HasSession = session is not null && phase is not SessionPhase.Idle and not SessionPhase.RequestPending;
        CanEndSession = phase is SessionPhase.Preparing or SessionPhase.Connecting or SessionPhase.Active;
        IsSessionEnded = phase == SessionPhase.Ended;

        SessionGuestText = session is null
            ? string.Empty
            : Tr.Format(UiStringKeys.MobileSessionGuestFormat,
                Tr.Format(UiStringKeys.SessionPeerFormat, session.PeerUserDisplayName, Tr.Ltr(session.PeerDeviceName)));
        SessionPhaseText = phase switch
        {
            SessionPhase.Preparing => Tr.Get(UiStringKeys.SessionPhasePreparing),
            SessionPhase.Connecting => Tr.Get(UiStringKeys.SessionPhaseConnecting),
            SessionPhase.Active => Tr.Get(UiStringKeys.SessionPhaseActive),
            SessionPhase.Ending => Tr.Get(UiStringKeys.SessionPhaseEnding),
            SessionPhase.Ended => Tr.Get(UiStringKeys.SessionPhaseEnded),
            _ => string.Empty,
        };

        var remaining = _sessions.TimeRemaining;
        SessionTimeText = remaining is null || phase != SessionPhase.Active
            ? string.Empty
            : remaining.Value <= TimeSpan.Zero
                ? Tr.Get(UiStringKeys.SessionTimeUp)
                : Tr.Format(UiStringKeys.SessionTimeRemainingFormat, Tr.Ltr(FormatDuration(remaining.Value)));
        // The host's "sent" is the guest's download: the coordinator counts from this side.
        SessionDataText = Tr.Format(UiStringKeys.SessionDataUsedFormat, FormatBytes(_sessions.BytesUp), FormatBytes(_sessions.BytesDown));
        SessionEndedText = IsSessionEnded
            ? Tr.Format(UiStringKeys.SessionSummaryFormat, DescribeEndReason(_sessions.LastEndReason), SessionDataText)
            : string.Empty;

        UpdateForegroundService();
    }

    [RelayCommand(CanExecute = nameof(CanEndSession))]
    private async Task EndSessionAsync()
    {
        try
        {
            await _sessions.DisconnectAsync();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Ending the session failed");
        }
    }

    [RelayCommand]
    private void DismissSession() => _sessions.Acknowledge();

    /// <summary>The foreground service runs while the phone is available or a session is live, and says which.</summary>
    private void UpdateForegroundService()
    {
        var live = _sessions.HasLiveSession;
        if (live)
        {
            // The data used, which changes with each session.stats; not the countdown, which would rewrite the
            // notification every second for as long as the session lasts.
            var guest = _sessions.CurrentSession?.PeerUserDisplayName ?? string.Empty;
            _platform.Hosting.Show(Tr.Format(UiStringKeys.MobileServiceSessionTitleFormat, guest), SessionDataText, sessionActive: true);
        }
        else if (IsAvailable)
        {
            _platform.Hosting.Show(Tr.Get(UiStringKeys.MobileServiceAvailableTitle), Tr.Get(UiStringKeys.MobileServiceAvailableText), sessionActive: false);
        }
        else
        {
            _platform.Hosting.Stop();
        }
    }

    internal static string DescribeEndReason(string? reason) => reason switch
    {
        null or "" => Tr.Get(UiStringKeys.SessionPhaseEnded),
        SessionEndReasonNames.HostEnded => Tr.Get(UiStringKeys.SessionEndedByYou),
        SessionEndReasonNames.GuestEnded => Tr.Get(UiStringKeys.SessionEndedByPeer),
        SessionEndReasonNames.Expired => Tr.Get(UiStringKeys.SessionEndedExpired),
        SessionEndReasonNames.HostDisconnected => Tr.Get(UiStringKeys.SessionEndedYouDisconnected),
        SessionEndReasonNames.GuestDisconnected => Tr.Get(UiStringKeys.SessionEndedPeerDisconnected),
        SessionEndReasonNames.ConnectFailed => Tr.Get(UiStringKeys.SessionEndedConnectFailed),
        SessionEndReasonNames.AdminTerminated => Tr.Get(UiStringKeys.SessionEndedAdminTerminated),
        SessionEndReasonNames.ProtocolError => Tr.Get(UiStringKeys.SessionEndedProtocolError),
        SessionEndReasonNames.ContactRemoved => Tr.Get(UiStringKeys.SessionEndedContactRemoved),
        _ => Tr.Format(UiStringKeys.SessionEndedUnknownFormat, reason),
    };

    internal static string FormatDuration(TimeSpan value)
    {
        if (value < TimeSpan.Zero) value = TimeSpan.Zero;
        return value >= TimeSpan.FromHours(1)
            ? value.ToString(@"h\:mm\:ss", Tr.Culture)
            : value.ToString(@"m\:ss", Tr.Culture);
    }

    internal static string FormatBytes(long bytes)
    {
        if (bytes < 0) bytes = 0;
        var (value, unit) = bytes switch
        {
            < 1024 => ((double)bytes, Tr.Get(UiStringKeys.BytesUnitB)),
            < 1024L * 1024 => (bytes / 1024d, Tr.Get(UiStringKeys.BytesUnitKb)),
            < 1024L * 1024 * 1024 => (bytes / (1024d * 1024), Tr.Get(UiStringKeys.BytesUnitMb)),
            _ => (bytes / (1024d * 1024 * 1024), Tr.Get(UiStringKeys.BytesUnitGb)),
        };
        return Tr.Format(UiStringKeys.BytesFormat, value, unit);
    }

    public void Dispose()
    {
        _channel.StateChanged -= OnChannelStateChanged;
        _channel.MessageReceived -= OnMessage;
        _sessions.PropertyChanged -= OnSessionChanged;
        _connector.Changed -= OnConnectorChanged;
        _platform.Network.Changed -= OnNetworkChanged;
        _platform.Permissions.Changed -= OnPermissionsChanged;
        Incoming?.Dispose();
    }
}
