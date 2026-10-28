using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Josour.App.Models;
using Josour.App.Services;
using Josour.Core.Control;
using Josour.Core.Session;
using Josour.Infrastructure.Api;
using Josour.Infrastructure.Contacts;
using Josour.Infrastructure.Control.Mock;
using Josour.Infrastructure.Diagnostics;
using Josour.Infrastructure.Session;
using Josour.App.Services.Notifications;

namespace Josour.App.ViewModels;

/// <summary>
/// Host page: the "Available for requests" state (single source of truth, mirrored by the tray) published as <c>host.available</c>
/// and only reported as available once the server confirms it with <c>host.state</c> (a host never appears in its own host list);
/// and incoming-request handling (<c>request.incoming</c> → prompt → <c>request.accept</c>/<c>request.reject</c>, <c>request.expired</c> closes it).
/// </summary>
public sealed partial class HostViewModel : ObservableObject
{
    private readonly IControlChannel _controlChannel;
    private readonly SessionCoordinator _sessions;
    private readonly IIncomingRequestPresenter _presenter;
    private readonly IAllowlistDisclosure _allowlist;
    private readonly HostReadinessMonitor _readiness;
    private readonly IAuthSession _auth;
    private readonly IContactsStore _contacts;
    private readonly IApiClient _api;
    private readonly INotifier _toasts;
    private readonly ILogger<HostViewModel> _logger;
    private bool _suppressPublish;
    private RequestIncomingMessage? _autoAccepted;

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

    /// <summary>Windows Firewall has no inbound rule for Josour, so the other device may never reach this one.</summary>
    [ObservableProperty]
    private bool _isFirewallRuleMissing;

    /// <summary>A VPN adapter holds the outbound route: the sites will see its exit address, not this machine's.</summary>
    [ObservableProperty]
    private bool _isVpnWarningVisible;

    /// <summary>The VPN warning with the adapter named; empty while <see cref="IsVpnWarningVisible"/> is false.</summary>
    [ObservableProperty]
    private string _vpnWarningMessage = string.Empty;

    public HostViewModel(
        IControlChannel controlChannel,
        SessionCoordinator sessions,
        IIncomingRequestPresenter presenter,
        IAllowlistDisclosure allowlist,
        HostReadinessMonitor readiness,
        IAuthSession auth,
        IContactsStore contacts,
        IApiClient api,
        INotifier toasts,
        ILogger<HostViewModel> logger)
    {
        _controlChannel = controlChannel;
        _sessions = sessions;
        _presenter = presenter;
        _allowlist = allowlist;
        _readiness = readiness;
        _auth = auth;
        _contacts = contacts;
        _api = api;
        _toasts = toasts;
        _logger = logger;

        _controlChannel.StateChanged += OnChannelStateChanged;
        _controlChannel.MessageReceived += OnMessage;
        _sessions.PropertyChanged += OnSessionPropertyChanged;
        IsConnected = _controlChannel.State == ControlChannelState.Connected;
        IsSimulatedServer = controlChannel is MockControlChannel;

        // A probe that already ran (the first run does one) is shown at once rather than repeated.
        if (_readiness.HasProbed)
        {
            ApplyReadiness(_readiness.Last);
        }
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
        if (value)
        {
            _ = CheckReadinessAsync(force: false);
        }
        else
        {
            // Not available: neither warning is about anything any more.
            IsVpnWarningVisible = false;
            IsFirewallRuleMissing = false;
        }

        _ = PublishAvailabilityAsync(value);
    }

    /// <summary>
    /// Plan 8.3 step 2, both halves. Before the host announces itself: the inbound firewall rule (a blocked listener looks
    /// exactly like a NAT failure later) and the VPN adapter (the sites would see the VPN's exit address, which defeats the
    /// point of borrowing this machine's address at all). Runs off the UI thread and never blocks the announcement — both
    /// answers are advisory, and the host may proceed with either warning showing.
    /// <para>
    /// It runs a second time, forced, when a session starts: a VPN can be brought up at any point between "available" and
    /// the first request, and the moment the traffic is about to flow is the one where the warning is worth something.
    /// The UPnP warm-up of that plan step belongs to the tunnel's <c>CandidateGatherer</c> and is not duplicated here.
    /// </para>
    /// </summary>
    private async Task CheckReadinessAsync(bool force)
    {
        try
        {
            var readiness = await _readiness.RefreshAsync(force, CancellationToken.None).ConfigureAwait(false);
            UiThread.Post(() => ApplyReadiness(readiness));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "The readiness check before announcing availability failed");
        }
    }

    /// <summary>
    /// Turns a probe into the two warnings on the page. Only <see cref="HostReadiness.ShouldWarnAboutVpn"/> — high
    /// confidence, i.e. an adapter that actually holds the outbound route — raises the VPN warning; a VPN client that is
    /// merely running changes nothing about the exit address, and warning about it would make the warning permanent.
    /// </summary>
    private void ApplyReadiness(HostReadiness readiness)
    {
        IsFirewallRuleMissing = readiness.IsFirewallRuleMissing;
        IsVpnWarningVisible = readiness.ShouldWarnAboutVpn;
        VpnWarningMessage = readiness.ShouldWarnAboutVpn
            ? string.Format(UiFlow.Culture, Strings.HostVpnWarningMessageFormat, UiFlow.Ltr(readiness.VpnAdapterName))
            : string.Empty;
    }

    /// <summary>A session is being prepared: ask again, because this is the moment the answer decides something.</summary>
    private void OnSessionPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SessionCoordinator.Phase) && _sessions.Phase == SessionPhase.Preparing)
        {
            _ = CheckReadinessAsync(force: true);
        }
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
            // No listen_port on the idle announcement, on purpose: the tunnel listener only exists between session.created and
            // session.connected (docs/protocol.md section 2), so an idle host has no port to advertise. The server therefore
            // leaves presence.reachable at null (docs/ws-protocol.md section 8) until it asks for a probe some other way.
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
            case HostStateMessage state:
                // The server's own record of this device: the only way a host can know its availability was taken,
                // because the host list it receives never contains itself.
                UiThread.Post(() => IsAnnounced = IsAvailable && state.Available);
                break;

            case HostsMessage hosts when hosts.Hosts.Any(h => h.DeviceId == _auth.DeviceId):
                // A server older than host.state still lists the host to itself.
                UiThread.Post(() => IsAnnounced = IsAvailable);
                break;

            case ErrorMessage { Code: ControlErrorCodes.TrustRevoked } when _autoAccepted is { } withdrawn:
                // The grant was withdrawn (from another device) while this one answered on its strength: ask after all.
                _autoAccepted = null;
                UiThread.Post(() => _ = ReceiveIncomingRequestAsync(withdrawn, CancellationToken.None, allowAuto: false));
                break;

            case RequestIncomingMessage incoming:
                UiThread.Post(() => _ = ReceiveIncomingRequestAsync(incoming, CancellationToken.None));
                break;

            case RequestExpiredMessage expired:
                UiThread.Post(() => _presenter.Expire(expired.RequestId));
                break;

            default:
                break;
        }
    }

    /// <summary>
    /// <c>request.incoming</c>. A trusted guest (docs/ws-protocol.md section 5a) is answered straight away; everyone
    /// else gets the pre-accept disclosure the product document (section 15) requires — the sites that request's
    /// <c>allowlist_version</c> permits, fetched with <c>GET /domains?version=N</c>. A fetch that fails does not block
    /// the prompt: it opens saying the list could not be loaded, which is not the same thing as an empty list.
    /// <para>
    /// The trusted-guest check comes <b>before</b> that fetch, deliberately. The fetch exists to show the host what it
    /// is agreeing to, and an auto-accepted request shows the host nothing — so waiting on it would spend seconds of
    /// the request's 60 building a screen nobody will see, and would let a slow or failing <c>GET /domains</c> hold up
    /// a decision that had already been taken. The version number the policy needs is in the frame itself.
    /// </para>
    /// </summary>
    public async Task ReceiveIncomingRequestAsync(RequestIncomingMessage incoming, CancellationToken ct, bool allowAuto = true)
    {
        ArgumentNullException.ThrowIfNull(incoming);

        // The coordinator saw the frame first. If it is not tracking this request (a session of ours is still live, or a
        // question of our own is pending), a yes could not be honoured: say nothing, and the server expires the request.
        if (!_sessions.IsAwaitingAnswer(incoming.RequestId))
        {
            _logger.LogWarning("Not presenting request {RequestId}: this device is busy with a session or another request", incoming.RequestId);
            return;
        }

        if (!TryBeginRequest(incoming.RequestId))
        {
            return;
        }

        try
        {
            if (allowAuto && await TryAutoAcceptAsync(incoming, ct).ConfigureAwait(true))
            {
                return;
            }

            if (!allowAuto)
            {
                _toasts.ShowInfo(Strings.AutoAcceptToastTitle, Strings.ErrorTrustRevoked);
            }

            // ADR-0010: with the list unenforced there is no bounded set of sites, and showing one would
            // ask the host to consent to something narrower than what actually happens.
            var allowlist = _sessions.EnforceAllowlist
                ? await _allowlist.DescribeAsync(incoming.AllowlistVersion, ct).ConfigureAwait(true)
                : AllowlistDisclosure.NoRestriction(incoming.AllowlistVersion);
            if (!allowlist.Loaded)
            {
                _logger.LogWarning(
                    "Showing request {RequestId} without its allow-list: version {Version} could not be loaded",
                    incoming.RequestId,
                    incoming.AllowlistVersion);
            }

            await PresentAndAnswerAsync(IncomingRequest.FromMessage(incoming, allowlist), ct).ConfigureAwait(true);
        }
        finally
        {
            HasPendingRequest = false;
        }
    }

    /// <summary>
    /// Presents an already-built request and answers the server. The debug simulator's entry point; the live path is
    /// <see cref="ReceiveIncomingRequestAsync"/>.
    /// </summary>
    public async Task HandleIncomingRequestAsync(IncomingRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!TryBeginRequest(request.RequestId))
        {
            return;
        }

        try
        {
            await PresentAndAnswerAsync(request, ct).ConfigureAwait(true);
        }
        finally
        {
            HasPendingRequest = false;
        }
    }

    /// <summary>One request on screen at a time. False means another one already is.</summary>
    private bool TryBeginRequest(Guid requestId)
    {
        if (HasPendingRequest)
        {
            _logger.LogWarning("Ignoring incoming request {RequestId}: another request is already being shown", requestId);
            return false;
        }

        HasPendingRequest = true;
        return true;
    }

    /// <summary>Shows the request to the host and sends whatever it decided. The caller owns <see cref="HasPendingRequest"/>.</summary>
    private async Task PresentAndAnswerAsync(IncomingRequest request, CancellationToken ct)
    {
        var answer = await _presenter.PresentAsync(request, ct);
        _logger.LogInformation("Incoming request {RequestId} from {GuestName}: {Decision}", request.RequestId, request.GuestName, answer.Decision);

        switch (answer.Decision)
        {
            case IncomingRequestDecision.Accepted:
                await AnswerAsync(() => _sessions.AcceptRequestAsync(request.RequestId, ct), "request.accept");
                // After the acceptance, never before it: a rule stored for a request that then failed to send would be
                // a standing consent the host gave for a session it never actually granted.
                if (answer.AlwaysAllow)
                {
                    await GrantTrustAsync(request, ct).ConfigureAwait(true);
                }

                break;
            case IncomingRequestDecision.Rejected:
                await AnswerAsync(() => _sessions.RejectRequestAsync(request.RequestId, ct), "request.reject");
                break;
            default:
                // TimedOut / Dismissed: the server expires the request itself and sends request.expired.
                break;
        }
    }

    /// <summary>
    /// ADR-0014. Answers <c>request.accept {auto: true}</c> and returns true when this contact may use the connection
    /// without asking; returns false - every other time - so the prompt is shown as usual.
    /// <para>
    /// Two independent sources must agree: the server marked the request <c>trusted</c>, <b>and</b> this app's own copy of
    /// the contact list (read from <c>GET /contacts</c>) says the host allowed it. The server checks the grant once more
    /// when the answer arrives, and answers <c>trust_revoked</c> if it was withdrawn meanwhile - which brings the prompt
    /// back. Nothing here may add a reason to accept; this method only carries out a decision the host already took.
    /// </para>
    /// </summary>
    private async Task<bool> TryAutoAcceptAsync(RequestIncomingMessage incoming, CancellationToken ct)
    {
        if (!incoming.Trusted || incoming.GuestUserId == Guid.Empty || !_contacts.Trusts(incoming.GuestUserId))
        {
            if (incoming.Trusted)
            {
                _logger.LogInformation(
                    "Request {RequestId} from {GuestName} is marked trusted but this app's contact list disagrees; asking the host",
                    incoming.RequestId,
                    incoming.GuestName);
            }

            return false;
        }

        _logger.LogInformation(
            "Request {RequestId} from {GuestName}: the host lets this contact connect without asking; accepting",
            incoming.RequestId,
            incoming.GuestName);

        var sent = false;
        _autoAccepted = incoming;
        await AnswerAsync(
            async () =>
            {
                await _sessions.AcceptRequestAsync(incoming.RequestId, auto: true, ct).ConfigureAwait(false);
                sent = true;
            },
            "request.accept (auto)");

        if (!sent)
        {
            // The accept never left the machine. Falling through to the prompt gives the host the request back
            // rather than letting it expire silently on a grant that was supposed to make things easier.
            _autoAccepted = null;
            return false;
        }

        // Inform, do not ask. A host who only ever learns about these from a bill or from a slow connection was not
        // told, and being told is the part of consent that an automatic acceptance must not spend.
        _toasts.ShowInfo(
            Strings.AutoAcceptToastTitle,
            string.Format(UiFlow.Culture, Strings.AutoAcceptToastBodyFormat, incoming.GuestName, incoming.GuestDevice));
        return true;
    }

    /// <summary>
    /// The host ticked "always let this contact use my connection" while accepting. Stored on the server
    /// (<c>PUT /contacts/{id}/trust</c>) after the acceptance went out, never before: a grant stored for a request that
    /// then failed to send would be a standing consent for a session the host never actually granted.
    /// </summary>
    private async Task GrantTrustAsync(IncomingRequest request, CancellationToken ct)
    {
        if (request.GuestUserId == Guid.Empty)
        {
            _logger.LogWarning("Cannot trust {GuestName}: this server does not identify guests", request.GuestName);
            return;
        }

        try
        {
            await _api.SetTrustAsync(request.GuestUserId, autoAccept: true, ct).ConfigureAwait(false);
            await _contacts.RefreshAsync(ct).ConfigureAwait(false);
            _logger.LogInformation("{GuestName} may now use this connection without asking", request.GuestName);
        }
        catch (Exception ex) when (ex is ApiException or ApiUnavailableException)
        {
            // The session is already granted; only the standing consent is lost. Say so in the log rather than letting
            // the host believe it will not be asked again.
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
            MockControlChannelOptions.SimulatedGuestUserId,
            MockControlChannelOptions.SimulatedGuestDeviceId,
            Strings.DebugSampleGuestName,
            Strings.DebugSampleGuestDevice,
            DurationMinutes: 30,
            ExpiresAt: DateTimeOffset.UtcNow.AddSeconds(60),
            new AllowlistDisclosure(1, new[] { "example.com", "=exact.com", "portal.corp:8443" }, Loaded: true));

        return HandleIncomingRequestAsync(request, CancellationToken.None);
    }
}
