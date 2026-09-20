using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Josour.App.Services;
using Josour.Core.Control;
using Josour.Core.Session;
using Josour.Infrastructure.Api;
using Josour.Infrastructure.Session;
using Josour.Proxy;

namespace Josour.App.ViewModels;

/// <summary>
/// Guest page: the live list of available hosts and the request flow.
/// <c>hosts.snapshot</c> / <c>hosts.update</c> from the control channel replace the list as it changes; Refresh is the REST
/// fallback (<c>GET /hosts</c>) for when the channel is not up yet. This device is never listed (you cannot browse through yourself).
/// "Request connection" sends <c>request.create</c> with the chosen duration, shows the waiting state with Cancel
/// (<c>request.cancel</c>) and reports <c>request.result</c> in plain words.
/// </summary>
public sealed partial class GuestViewModel : ObservableObject
{
    /// <summary>Offered session lengths (docs/plan 8.5); the list is cut to <c>hello.ack settings.max_session_minutes</c>.</summary>
    public static readonly IReadOnlyList<int> OfferedDurations = new[] { 15, 30, 60, 120 };

    private const int DefaultMaxSessionMinutes = 120;

    private readonly IApiClient _api;
    private readonly IAuthSession _auth;
    private readonly IControlChannel _controlChannel;
    private readonly SessionCoordinator _sessions;
    private readonly ControlChannelConnector _connector;
    private readonly ILogger<GuestViewModel> _logger;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand))]
    private bool _isRefreshing;

    [ObservableProperty]
    private string _emptyStateTitle = Strings.GuestNoHostsTitle;

    [ObservableProperty]
    private string _emptyStateText = Strings.GuestNoHostsText;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RequestConnectionCommand))]
    private HostListItem? _selectedHost;

    [ObservableProperty]
    private int _selectedDuration = 30;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RequestConnectionCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelRequestCommand))]
    private bool _isWaitingForHost;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RequestConnectionCommand))]
    private bool _isConnected;

    [ObservableProperty]
    private string _waitingText = string.Empty;

    /// <summary>The last outcome in plain words (accepted / declined / expired / server error); empty when there is nothing to say.</summary>
    [ObservableProperty]
    private string _statusMessage = string.Empty;

    public GuestViewModel(
        IApiClient api,
        IAuthSession auth,
        IControlChannel controlChannel,
        SessionCoordinator sessions,
        ControlChannelConnector connector,
        ILogger<GuestViewModel> logger)
    {
        _api = api;
        _auth = auth;
        _controlChannel = controlChannel;
        _sessions = sessions;
        _connector = connector;
        _logger = logger;

        Hosts.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasHosts));
        _controlChannel.MessageReceived += OnMessage;
        _controlChannel.StateChanged += OnChannelStateChanged;
        _sessions.PropertyChanged += OnSessionsPropertyChanged;
        _auth.Changed += (_, _) => UiThread.Post(() =>
        {
            if (!_auth.IsSignedIn)
            {
                Hosts.Clear();
                SetEmptyState(Strings.GuestNoHostsTitle, Strings.GuestNotSignedInText);
            }
        });

        IsConnected = _controlChannel.State == ControlChannelState.Connected;
        ApplyServerSettings(_connector.LastHello);
    }

    public ObservableCollection<HostListItem> Hosts { get; } = new();

    /// <summary>Session lengths the user may pick, capped by the server's <c>max_session_minutes</c>.</summary>
    public ObservableCollection<int> DurationOptions { get; } = new(OfferedDurations);

    public bool HasHosts => Hosts.Count > 0;

    /// <summary>
    /// Whether this machine can be a guest at all.
    /// <para>
    /// It cannot unless the platform can tell which process opened a local connection: the proxy listens on
    /// loopback, and without that check it would carry ANY program's traffic out through the host's address —
    /// not the work browser's alone. The host agreed to lend a browser, not a machine. So where the check does
    /// not exist the role is refused outright rather than offered in a weaker form, because a weaker form is
    /// one the host was never asked about.
    /// </para>
    /// <para>
    /// Windows and macOS both have an answer now — <c>GetExtendedTcpTable</c> and <c>lsof</c> — so this is false
    /// only on a platform that has neither.
    /// </para>
    /// </summary>
    public static bool IsGuestRoleSupported => OwnerPidCheckers.SupportedOnThisPlatform;

    /// <summary>The inverse, for the warning bar that takes the page's place when the role is unavailable.</summary>
    public static bool IsGuestRoleUnavailable => !IsGuestRoleSupported;

    public bool HasStatusMessage => StatusMessage.Length > 0;

    private bool CanRefresh() => !IsRefreshing;

    private bool CanRequestConnection() => IsGuestRoleSupported && SelectedHost is not null && IsConnected && !IsWaitingForHost;

    private bool CanCancelRequest() => IsWaitingForHost;

    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private async Task RefreshAsync(CancellationToken ct)
    {
        if (!_auth.IsSignedIn)
        {
            SetEmptyState(Strings.GuestNoHostsTitle, Strings.GuestNotSignedInText);
            return;
        }

        IsRefreshing = true;
        try
        {
            _logger.LogInformation("Guest: refreshing hosts via GET /hosts (control channel {State})", _controlChannel.State);
            var hosts = await _api.GetHostsAsync(ct).ConfigureAwait(true);
            ApplyHosts(hosts);
            SetEmptyState(Strings.GuestNoHostsTitle, Strings.GuestNoHostsText);
        }
        catch (ApiUnavailableException ex)
        {
            _logger.LogWarning("Guest: hosts unavailable: {Reason}", ex.Message);
            Hosts.Clear();
            SetEmptyState(Strings.GuestHostsUnavailableTitle, Strings.GuestHostsUnavailableText);
        }
        catch (ApiException ex)
        {
            _logger.LogWarning("Guest: GET /hosts failed: {Status} {Code}", (int)ex.StatusCode, ex.Code);
            Hosts.Clear();
            SetEmptyState(Strings.GuestHostsUnavailableTitle, string.Format(UiFlow.Culture, Strings.GuestHostsErrorFormat, ex.Message));
        }
        catch (OperationCanceledException)
        {
            // page closed
        }
        finally
        {
            IsRefreshing = false;
        }
    }

    /// <summary>Sends <c>request.create</c> for the selected host and enters the waiting state.</summary>
    [RelayCommand(CanExecute = nameof(CanRequestConnection))]
    private async Task RequestConnectionAsync(CancellationToken ct)
    {
        var host = SelectedHost;
        if (host is null)
        {
            return;
        }

        var duration = SelectedDuration;
        StatusMessage = string.Empty;
        OnPropertyChanged(nameof(HasStatusMessage));
        WaitingText = string.Format(UiFlow.Culture, Strings.GuestWaitingTextFormat, host.UserDisplayName);

        _logger.LogInformation("Guest: requesting {DurationMin} min from {DeviceName} ({DeviceId})", duration, host.DeviceName, host.DeviceId);
        var error = await _sessions.RequestSessionAsync(host.DeviceId, duration, ct).ConfigureAwait(true);
        if (error is not null)
        {
            SetStatus(DescribeError(error));
            return;
        }

        SetStatus(string.Format(UiFlow.Culture, Strings.GuestRequestSentFormat, host.UserDisplayName, duration));
    }

    /// <summary>Sends <c>request.cancel</c> for the pending request.</summary>
    [RelayCommand(CanExecute = nameof(CanCancelRequest))]
    private async Task CancelRequestAsync(CancellationToken ct)
    {
        try
        {
            await _sessions.CancelRequestAsync(ct).ConfigureAwait(true);
            SetStatus(Strings.GuestRequestCancelled);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Guest: request.cancel failed");
            SetStatus(Strings.GuestErrorNotConnected);
        }
    }

    /// <summary>Replaces the list with the server's full snapshot (call on the UI thread). This device is filtered out.</summary>
    public void ApplyHosts(IReadOnlyList<HostInfoDto> hosts)
    {
        ArgumentNullException.ThrowIfNull(hosts);
        var self = _auth.DeviceId;
        var selected = SelectedHost?.DeviceId;

        Hosts.Clear();
        foreach (var host in hosts)
        {
            if (self is not null && host.DeviceId == self)
            {
                continue;
            }

            Hosts.Add(new HostListItem(host.DeviceId, host.UserDisplayName, host.DeviceName, host.Reachable));
        }

        SelectedHost = Hosts.FirstOrDefault(h => h.DeviceId == selected);
        _logger.LogInformation("Guest: {HostCount} hosts listed", Hosts.Count);
    }

    /// <summary>Caps <see cref="DurationOptions"/> with the server's <c>settings.max_session_minutes</c> (call on the UI thread).</summary>
    public void ApplyServerSettings(HelloAckMessage? ack)
    {
        var max = ack?.Settings.MaxSessionMinutes ?? DefaultMaxSessionMinutes;
        var allowed = OfferedDurations.Where(d => d <= max).ToList();
        if (allowed.Count == 0)
        {
            // A server that allows less than the shortest offer still gets one usable choice.
            allowed.Add(Math.Max(1, max));
        }

        if (DurationOptions.SequenceEqual(allowed))
        {
            return;
        }

        DurationOptions.Clear();
        foreach (var duration in allowed)
        {
            DurationOptions.Add(duration);
        }

        if (!DurationOptions.Contains(SelectedDuration))
        {
            SelectedDuration = DurationOptions.Contains(30) ? 30 : DurationOptions[^1];
        }
    }

    private void OnMessage(ControlMessage message)
    {
        switch (message)
        {
            case HostsMessage hosts:
                UiThread.Post(() =>
                {
                    ApplyHosts(hosts.Hosts);
                    SetEmptyState(Strings.GuestNoHostsTitle, Strings.GuestNoHostsText);
                });
                break;

            case HelloAckMessage ack:
                UiThread.Post(() => ApplyServerSettings(ack));
                break;

            case RequestResultMessage result:
                UiThread.Post(() => SetStatus(DescribeResult(result)));
                break;

            case RequestExpiredMessage:
                UiThread.Post(() => SetStatus(Strings.GuestRequestExpiredText));
                break;

            default:
                break;
        }
    }

    private void OnChannelStateChanged(ControlChannelState state) => UiThread.Post(() =>
    {
        IsConnected = state == ControlChannelState.Connected;
        if (state == ControlChannelState.Connected)
        {
            ApplyServerSettings(_connector.LastHello);
            return;
        }

        Hosts.Clear();
        SetEmptyState(Strings.GuestNoHostsTitle, _auth.IsSignedIn ? Strings.GuestHostsUnavailableText : Strings.GuestNotSignedInText);
    });

    private void OnSessionsPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(SessionCoordinator.Phase))
        {
            return;
        }

        var phase = _sessions.Phase;
        UiThread.Post(() =>
        {
            IsWaitingForHost = phase == SessionPhase.RequestPending;
            if (phase == SessionPhase.Preparing)
            {
                SetStatus(Strings.GuestRequestAccepted);
            }
        });
    }

    private static string DescribeResult(RequestResultMessage result) => result switch
    {
        { Accepted: true } => Strings.GuestRequestAccepted,
        { Reason: "expired" } => Strings.GuestRequestExpiredText,
        { Reason: "cancelled" } => Strings.GuestRequestCancelled,
        { Reason: "host_unavailable" } => Strings.GuestErrorHostUnavailable,
        _ => Strings.GuestRequestRejected,
    };

    private static string DescribeError(string code) => code switch
    {
        ControlErrorCodes.HostUnavailable => Strings.GuestErrorHostUnavailable,
        ControlErrorCodes.SessionExists => Strings.GuestErrorSessionExists,
        ControlErrorCodes.RequestPending or SessionCoordinator.BusyCode => Strings.GuestErrorRequestPending,
        ControlErrorCodes.RateLimited => Strings.GuestErrorRateLimited,
        SessionCoordinator.NotConnectedCode => Strings.GuestErrorNotConnected,
        SessionCoordinator.TimeoutCode => Strings.GuestRequestDisconnected,
        _ => string.Format(UiFlow.Culture, Strings.GuestErrorGenericFormat, code),
    };

    private void SetStatus(string message)
    {
        StatusMessage = message;
        OnPropertyChanged(nameof(HasStatusMessage));
    }

    private void SetEmptyState(string title, string text)
    {
        EmptyStateTitle = title;
        EmptyStateText = text;
    }
}
