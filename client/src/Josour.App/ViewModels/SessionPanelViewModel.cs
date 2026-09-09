using System.ComponentModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Josour.App.Services;
using Josour.Core.Browser;
using Josour.Core.Control;
using Josour.Core.Session;
using Josour.Infrastructure.Session;

namespace Josour.App.ViewModels;

/// <summary>
/// The session panel shown over both tabs while a session is Preparing / Connecting / Active, and after it ended until the
/// user dismisses the summary. It is a pure projection of <see cref="SessionCoordinator"/> into display strings
/// (all of them from <see cref="Strings"/>): peer, phase, the monotonic countdown, the approximate data used, Disconnect,
/// and — for the guest — "Reopen work browser" when the window was closed by hand.
/// </summary>
public sealed partial class SessionPanelViewModel : ObservableObject, IDisposable
{
    private readonly SessionCoordinator _sessions;
    private readonly ILogger<SessionPanelViewModel> _logger;

    [ObservableProperty]
    private bool _isVisible;

    [ObservableProperty]
    private string _peerText = string.Empty;

    [ObservableProperty]
    private string _roleText = string.Empty;

    [ObservableProperty]
    private string _phaseText = string.Empty;

    [ObservableProperty]
    private string _countdownText = string.Empty;

    [ObservableProperty]
    private bool _hasCountdown;

    [ObservableProperty]
    private string _dataUsedText = string.Empty;

    [ObservableProperty]
    private bool _isActive;

    [ObservableProperty]
    private bool _isEnded;

    [ObservableProperty]
    private string _endedText = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ReopenBrowserCommand))]
    private bool _canReopenBrowser;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DisconnectCommand))]
    private bool _canDisconnect;

    [ObservableProperty]
    private string _browserMessage = string.Empty;

    [ObservableProperty]
    private bool _hasBrowserMessage;

    public SessionPanelViewModel(SessionCoordinator sessions, ILogger<SessionPanelViewModel> logger)
    {
        _sessions = sessions;
        _logger = logger;
        _sessions.PropertyChanged += OnSessionsPropertyChanged;
        Refresh();
    }

    private void OnSessionsPropertyChanged(object? sender, PropertyChangedEventArgs e) => UiThread.Post(Refresh);

    /// <summary>Re-reads every coordinator property; cheap enough to run on any change and keeps the projection in one place.</summary>
    public void Refresh()
    {
        var phase = _sessions.Phase;
        var session = _sessions.CurrentSession;

        IsVisible = session is not null && phase is not SessionPhase.Idle and not SessionPhase.RequestPending;
        IsActive = phase == SessionPhase.Active;
        IsEnded = phase == SessionPhase.Ended;
        CanDisconnect = phase is SessionPhase.Preparing or SessionPhase.Connecting or SessionPhase.Active;

        // The device name is a technical identifier: isolated so it does not reorder inside right-to-left prose.
        PeerText = session is null
            ? string.Empty
            : string.Format(UiFlow.Culture, Strings.SessionPeerFormat, session.PeerUserDisplayName, UiFlow.Ltr(session.PeerDeviceName));
        RoleText = session is null ? string.Empty : session.IsHost ? Strings.SessionRoleHost : Strings.SessionRoleGuest;
        PhaseText = DescribePhase(phase);

        var remaining = _sessions.TimeRemaining;
        HasCountdown = remaining is not null && phase is SessionPhase.Active;
        CountdownText = remaining is null
            ? string.Empty
            : remaining.Value <= TimeSpan.Zero
                ? Strings.SessionTimeUp
                // "29:59" is a colon-separated technical value; without the isolate it reorders in Arabic.
                : string.Format(UiFlow.Culture, Strings.SessionTimeRemainingFormat, UiFlow.Ltr(FormatDuration(remaining.Value)));

        DataUsedText = string.Format(
            UiFlow.Culture,
            Strings.SessionDataUsedFormat,
            FormatBytes(_sessions.BytesUp),
            FormatBytes(_sessions.BytesDown));

        CanReopenBrowser = _sessions.CanReopenBrowser;

        var browser = DescribeBrowserFailure(_sessions.BrowserFailure);
        HasBrowserMessage = browser is not null;
        BrowserMessage = browser ?? string.Empty;

        EndedText = IsEnded
            ? string.Format(UiFlow.Culture, Strings.SessionSummaryFormat, DescribeEndReason(_sessions.LastEndReason, session?.IsHost ?? false), DataUsedText)
            : string.Empty;
    }

    [RelayCommand(CanExecute = nameof(CanDisconnect))]
    private async Task DisconnectAsync()
    {
        try
        {
            await _sessions.DisconnectAsync().ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Disconnecting the session failed");
        }
    }

    [RelayCommand(CanExecute = nameof(CanReopenBrowser))]
    private async Task ReopenBrowserAsync(CancellationToken ct)
    {
        try
        {
            await _sessions.ReopenBrowserAsync(ct).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Reopening the work browser failed");
        }
    }

    [RelayCommand]
    private void Dismiss() => _sessions.Acknowledge();

    private static string DescribePhase(SessionPhase phase) => phase switch
    {
        SessionPhase.Preparing => Strings.SessionPhasePreparing,
        SessionPhase.Connecting => Strings.SessionPhaseConnecting,
        SessionPhase.Active => Strings.SessionPhaseActive,
        SessionPhase.Ending => Strings.SessionPhaseEnding,
        SessionPhase.Ended => Strings.SessionPhaseEnded,
        _ => string.Empty,
    };

    /// <summary>The wire reason of <c>session.terminate</c> / our own end, in plain words for the summary.</summary>
    public static string DescribeEndReason(string? reason, bool isHost) => reason switch
    {
        null or "" => Strings.SessionPhaseEnded,
        SessionEndReasonNames.HostEnded => isHost ? Strings.SessionEndedByYou : Strings.SessionEndedByPeer,
        SessionEndReasonNames.GuestEnded => isHost ? Strings.SessionEndedByPeer : Strings.SessionEndedByYou,
        SessionEndReasonNames.Expired => Strings.SessionEndedExpired,
        SessionEndReasonNames.HostDisconnected => isHost ? Strings.SessionEndedYouDisconnected : Strings.SessionEndedPeerDisconnected,
        SessionEndReasonNames.GuestDisconnected => isHost ? Strings.SessionEndedPeerDisconnected : Strings.SessionEndedYouDisconnected,
        SessionEndReasonNames.ConnectFailed => Strings.SessionEndedConnectFailed,
        SessionEndReasonNames.AdminTerminated => Strings.SessionEndedAdminTerminated,
        SessionEndReasonNames.BrowserNotProxied => Strings.SessionEndedBrowserNotProxied,
        SessionEndReasonNames.ProtocolError => Strings.SessionEndedProtocolError,
        _ => string.Format(UiFlow.Culture, Strings.SessionEndedUnknownFormat, reason),
    };

    /// <summary>Why the work browser is unusable, naming the cause the launch result reported.</summary>
    public static string? DescribeBrowserFailure(BrowserLaunchFailure? failure) => failure switch
    {
        null => null,
        BrowserLaunchFailure.NotFound => Strings.BrowserErrorNotFound,
        BrowserLaunchFailure.ManagedByPolicy => Strings.BrowserErrorManagedByPolicy,
        BrowserLaunchFailure.InstanceHandoff => Strings.BrowserErrorInstanceHandoff,
        BrowserLaunchFailure.ProbeTimeout => Strings.BrowserErrorProbeTimeout,
        _ => Strings.BrowserErrorOther,
    };

    /// <summary>h:mm:ss above an hour, m:ss below it.</summary>
    public static string FormatDuration(TimeSpan value)
    {
        if (value < TimeSpan.Zero)
        {
            value = TimeSpan.Zero;
        }

        return value >= TimeSpan.FromHours(1)
            ? value.ToString(@"h\:mm\:ss", UiFlow.Culture)
            : value.ToString(@"m\:ss", UiFlow.Culture);
    }

    /// <summary>Approximate size for the panel: one decimal, the largest unit that keeps the number readable.</summary>
    public static string FormatBytes(long bytes)
    {
        if (bytes < 0)
        {
            bytes = 0;
        }

        const double Kb = 1024d;
        const double Mb = Kb * 1024d;
        const double Gb = Mb * 1024d;

        var (value, unit) = bytes switch
        {
            < 1024 => (bytes, Strings.BytesUnitB),
            < 1024L * 1024 => (bytes / Kb, Strings.BytesUnitKb),
            < 1024L * 1024 * 1024 => (bytes / Mb, Strings.BytesUnitMb),
            _ => (bytes / Gb, Strings.BytesUnitGb),
        };

        return string.Format(UiFlow.Culture, Strings.BytesFormat, value, unit);
    }

    public void Dispose() => _sessions.PropertyChanged -= OnSessionsPropertyChanged;
}
