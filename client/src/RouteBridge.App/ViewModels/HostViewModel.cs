using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using RouteBridge.App.Models;
using RouteBridge.App.Services;
using RouteBridge.Core.Control;
using RouteBridge.Core.Session;
using RouteBridge.Infrastructure.Api;
using RouteBridge.Infrastructure.Control.Mock;

namespace RouteBridge.App.ViewModels;

/// <summary>
/// Host page: the "Available for requests" state (single source of truth, mirrored by the tray) published as <c>host.available</c>
/// and only reported as available once the server echoes this device in <c>hosts.snapshot</c>/<c>hosts.update</c>;
/// and incoming-request handling (<c>request.incoming</c> → prompt → <c>request.accept</c>/<c>request.reject</c>, <c>request.expired</c> closes it).
/// </summary>
public sealed partial class HostViewModel : ObservableObject
{
    private readonly IControlChannel _controlChannel;
    private readonly SessionCoordinator _sessions;
    private readonly IIncomingRequestPresenter _presenter;
    private readonly IAuthSession _auth;
    private readonly ILogger<HostViewModel> _logger;
    private bool _suppressPublish;

    [ObservableProperty]
    private bool _isAvailable;

    [ObservableProperty]
    private string _statusText = Strings.HostStatusNotAvailable;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SimulateIncomingRequestCommand))]
    private bool _hasPendingRequest;

    [ObservableProperty]
    private bool _isConnected;

    /// <summary>The server has this device in its host list, i.e. <c>host.available</c> took effect.</summary>
    [ObservableProperty]
    private bool _isAnnounced;

    public HostViewModel(
        IControlChannel controlChannel,
        SessionCoordinator sessions,
        IIncomingRequestPresenter presenter,
        IAuthSession auth,
        ILogger<HostViewModel> logger)
    {
        _controlChannel = controlChannel;
        _sessions = sessions;
        _presenter = presenter;
        _auth = auth;
        _logger = logger;

        _controlChannel.StateChanged += OnChannelStateChanged;
        _controlChannel.MessageReceived += OnMessage;
        IsConnected = _controlChannel.State == ControlChannelState.Connected;
        IsSimulatedServer = controlChannel is MockControlChannel;
    }

    /// <summary>True for the <c>--mock</c> build: the page then says the server is simulated.</summary>
    public bool IsSimulatedServer { get; }

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

        _logger.LogInformation("Host availability set to {Available} (control channel {State})", value, _controlChannel.State);
        _ = PublishAvailabilityAsync(value);
    }

    partial void OnIsAnnouncedChanged(bool value) => UpdateStatusText();

    /// <summary><c>host.available</c>. Deferred while disconnected; re-sent when the channel (re)connects.</summary>
    private async Task PublishAvailabilityAsync(bool available)
    {
        if (_controlChannel.State != ControlChannelState.Connected)
        {
            _logger.LogInformation("host.available={Available} deferred until the control channel is connected", available);
            return;
        }

        try
        {
            // WEEK 5: FirewallRuleChecker + VpnAdapterDetector warnings before announcing; UPnP warm-up.
            // WEEK 4: listen_port from ITunnelSession.PrepareAsync so the server can run the reachability probe.
            await _controlChannel.SendAsync(new HostAvailableMessage(available, ListenPort: null), CancellationToken.None);
            _logger.LogInformation("host.available={Available} sent", available);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Could not send host.available={Available}", available);
            UiThread.Post(() =>
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

                StatusText = Strings.HostStatusAvailabilityFailed;
            });
        }
    }

    private void UpdateStatusText() => StatusText = (IsAvailable, IsAnnounced) switch
    {
        (false, _) => Strings.HostStatusNotAvailable,
        (true, true) => Strings.HostStatusAvailable,
        (true, false) => Strings.HostStatusAnnouncing,
    };

    private void OnChannelStateChanged(ControlChannelState state) => UiThread.Post(() =>
    {
        IsConnected = state == ControlChannelState.Connected;
        if (!IsConnected)
        {
            IsAnnounced = false; // the server forgets our presence the moment the socket drops
            return;
        }

        if (IsAvailable)
        {
            // The channel re-announces after its own reconnect; this covers "the user toggled while offline".
            _ = PublishAvailabilityAsync(true);
        }
    });

    private void OnMessage(ControlMessage message)
    {
        switch (message)
        {
            case HostsMessage hosts:
                var self = _auth.DeviceId;
                var listed = self is not null && hosts.Hosts.Any(h => h.DeviceId == self.Value);

                // A host with a session in progress is deliberately not listed (docs/ws-protocol.md section 5),
                // so only an idle device may conclude "the server did not take my host.available".
                UiThread.Post(() =>
                {
                    if (listed)
                    {
                        IsAnnounced = IsAvailable;
                    }
                    else if (_sessions.Phase == SessionPhase.Idle)
                    {
                        IsAnnounced = false;
                    }
                });
                break;

            case RequestIncomingMessage incoming:
                // WEEK 5: resolve the allow-list text for incoming.AllowlistVersion via GET /domains?version=N.
                var request = IncomingRequest.FromMessage(incoming, Strings.AllowedSitesPlaceholder);
                UiThread.Post(() => _ = HandleIncomingRequestAsync(request, CancellationToken.None));
                break;

            case RequestExpiredMessage expired:
                UiThread.Post(() => _presenter.Expire(expired.RequestId));
                break;

            default:
                break;
        }
    }

    /// <summary>Presents the request to the host and answers the server.</summary>
    public async Task HandleIncomingRequestAsync(IncomingRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (HasPendingRequest)
        {
            _logger.LogWarning("Ignoring incoming request {RequestId}: another request is already being shown", request.RequestId);
            return;
        }

        HasPendingRequest = true;
        try
        {
            var decision = await _presenter.PresentAsync(request, ct);
            _logger.LogInformation("Incoming request {RequestId} from {GuestName}: {Decision}", request.RequestId, request.GuestName, decision);

            switch (decision)
            {
                case IncomingRequestDecision.Accepted:
                    await AnswerAsync(() => _sessions.AcceptRequestAsync(request.RequestId, ct), "request.accept");
                    break;
                case IncomingRequestDecision.Rejected:
                    await AnswerAsync(() => _sessions.RejectRequestAsync(request.RequestId, ct), "request.reject");
                    break;
                default:
                    // TimedOut / Dismissed: the server expires the request itself and sends request.expired.
                    break;
            }
        }
        finally
        {
            HasPendingRequest = false;
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

    private bool CanSimulateIncomingRequest() => !HasPendingRequest;

    /// <summary>
    /// Debug menu. With the mock channel connected the request travels the real path (<c>request.incoming</c> → prompt → <c>request.accept</c>
    /// → <c>session.created</c> …); otherwise the prompt is shown directly with sample data so the window/toast can still be checked.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanSimulateIncomingRequest))]
    private Task SimulateIncomingRequestAsync()
    {
        if (_controlChannel is MockControlChannel mock && mock.State == ControlChannelState.Connected)
        {
            var id = mock.SimulateIncomingRequest(Strings.DebugSampleGuestName, Strings.DebugSampleGuestDevice, 30);
            _logger.LogInformation("Debug: simulated request.incoming {RequestId}", id);
            return Task.CompletedTask;
        }

        var request = new IncomingRequest(
            Guid.NewGuid(),
            Strings.DebugSampleGuestName,
            Strings.DebugSampleGuestDevice,
            DurationMinutes: 30,
            ExpiresAt: DateTimeOffset.UtcNow.AddSeconds(60),
            Strings.AllowedSitesPlaceholder);

        return HandleIncomingRequestAsync(request, CancellationToken.None);
    }
}
