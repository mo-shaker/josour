using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using RouteBridge.App.Models;
using RouteBridge.App.Services;
using RouteBridge.Core.Control;

namespace RouteBridge.App.ViewModels;

/// <summary>Host page: the "Available for requests" state (single source of truth, mirrored by the tray) and incoming-request handling.</summary>
public sealed partial class HostViewModel : ObservableObject
{
    private readonly IControlChannel _controlChannel;
    private readonly IIncomingRequestPresenter _presenter;
    private readonly ILogger<HostViewModel> _logger;

    [ObservableProperty]
    private bool _isAvailable;

    [ObservableProperty]
    private string _statusText = Strings.HostStatusNotAvailable;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SimulateIncomingRequestCommand))]
    private bool _hasPendingRequest;

    public HostViewModel(IControlChannel controlChannel, IIncomingRequestPresenter presenter, ILogger<HostViewModel> logger)
    {
        _controlChannel = controlChannel;
        _presenter = presenter;
        _logger = logger;
    }

    partial void OnIsAvailableChanged(bool value)
    {
        StatusText = value ? Strings.HostStatusAvailable : Strings.HostStatusNotAvailable;
        _logger.LogInformation("Host availability set to {Available} (control channel {State})", value, _controlChannel.State);

        // WEEK 2: HostViewModel.PublishAvailabilityAsync — IControlChannel.SendAsync(new HostAvailableMessage(value, listenPort: null))
        // after FirewallRuleChecker / VpnAdapterDetector warnings; listen_port is filled in week 4 from ITunnelSession.PrepareAsync.
    }

    /// <summary>Presents the request to the host and (from week 2) answers the server.</summary>
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
                    // WEEK 2: HostViewModel.AcceptRequestAsync — IControlChannel.RequestAsync(new RequestAcceptMessage(ref, request.RequestId), timeout, ct), then await session.created.
                    break;
                case IncomingRequestDecision.Rejected:
                    // WEEK 2: HostViewModel.RejectRequestAsync — IControlChannel.RequestAsync(new RequestRejectMessage(ref, request.RequestId), timeout, ct).
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

    private bool CanSimulateIncomingRequest() => !HasPendingRequest;

    /// <summary>Debug menu: shows the incoming-request window + toast with sample data so QA can see the flow before the server exists.</summary>
    [RelayCommand(CanExecute = nameof(CanSimulateIncomingRequest))]
    private Task SimulateIncomingRequestAsync()
    {
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
