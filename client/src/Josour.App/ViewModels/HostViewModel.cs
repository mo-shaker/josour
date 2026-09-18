using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Josour.App.Models;
using Josour.App.Services;
using Josour.Core.Control;
using Josour.Core.Session;
using Josour.Infrastructure.Api;
using Josour.Infrastructure.Control.Mock;
using Josour.Infrastructure.Diagnostics;
using Josour.Infrastructure.Session;
using Josour.Infrastructure.Settings;
using Josour.App.Services.Notifications;

namespace Josour.App.ViewModels;

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
    private readonly IAllowlistDisclosure _allowlist;
    private readonly HostReadinessMonitor _readiness;
    private readonly IAuthSession _auth;
    private readonly IAutoAcceptStore _autoAccept;
    private readonly INotifier _toasts;
    private readonly TimeProvider _time;
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
        IAutoAcceptStore autoAccept,
        INotifier toasts,
        ILogger<HostViewModel> logger,
        TimeProvider? time = null)
    {
        _controlChannel = controlChannel;
        _sessions = sessions;
        _presenter = presenter;
        _allowlist = allowlist;
        _readiness = readiness;
        _auth = auth;
        _autoAccept = autoAccept;
        _toasts = toasts;
        _time = time ?? TimeProvider.System;
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
    public async Task ReceiveIncomingRequestAsync(RequestIncomingMessage incoming, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(incoming);
        if (!TryBeginRequest(incoming.RequestId))
        {
            return;
        }

        try
        {
            if (await TryAutoAcceptAsync(
                    incoming.RequestId,
                    incoming.GuestUserId,
                    incoming.GuestDeviceId,
                    incoming.GuestName,
                    incoming.GuestDevice,
                    incoming.DurationMin,
                    incoming.AllowlistVersion,
                    ct).ConfigureAwait(true))
            {
                return;
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
            if (await TryAutoAcceptAsync(
                    request.RequestId,
                    request.GuestUserId,
                    request.GuestDeviceId,
                    request.GuestName,
                    request.GuestDevice,
                    request.DurationMinutes,
                    request.Allowlist.Version,
                    ct).ConfigureAwait(true))
            {
                return;
            }

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
                if (answer.Trust is { } trust)
                {
                    await GrantTrustAsync(request, trust, ct).ConfigureAwait(true);
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
    /// docs/ws-protocol.md section 5a. Answers <c>request.accept {auto: true}</c> and returns true when the host had
    /// already decided about this guest; returns false — every other time — so the prompt is shown as usual.
    /// <para>
    /// The whole decision is <see cref="AutoAcceptPolicy"/>, which is pure and lives in <c>Josour.Core</c> where it can
    /// be tested exhaustively. Nothing here may add a reason to accept; this method only carries the answer out.
    /// </para>
    /// </summary>
    private async Task<bool> TryAutoAcceptAsync(
        Guid requestId,
        Guid guestUserId,
        Guid guestDeviceId,
        string guestName,
        string guestDevice,
        int durationMinutes,
        int allowlistVersion,
        CancellationToken ct)
    {
        var settings = _autoAccept.Current;
        var verdict = AutoAcceptPolicy.Evaluate(
            settings,
            guestUserId,
            guestDeviceId,
            durationMinutes,
            allowlistVersion,
            _sessions.EnforceAllowlist,
            _time.GetUtcNow());

        if (!verdict.ShouldAccept)
        {
            // Only worth a line once the host has actually switched the feature on: otherwise it is every request on
            // every device saying "disabled".
            if (settings.Enabled)
            {
                _logger.LogInformation(
                    "Request {RequestId} from {GuestName} was not auto-accepted ({Outcome}); asking the host",
                    requestId,
                    guestName,
                    verdict.Outcome);
            }

            return false;
        }

        _logger.LogInformation(
            "Request {RequestId} from {GuestName} matched a trusted-guest rule; accepting without a prompt",
            requestId,
            guestName);

        var sent = false;
        await AnswerAsync(
            async () =>
            {
                await _sessions.AcceptRequestAsync(requestId, auto: true, ct).ConfigureAwait(false);
                sent = true;
            },
            "request.accept (auto)");

        if (!sent)
        {
            // The accept never left the machine. Falling through to the prompt gives the host the request back
            // rather than letting it expire silently on a rule that was supposed to make things easier.
            return false;
        }

        // Inform, do not ask (section 5a). A host who only ever learns about these from a bill or from a slow
        // connection was not told, and being told is the part of consent that auto-accept must not spend.
        _toasts.ShowInfo(
            Strings.AutoAcceptToastTitle,
            string.Format(UiFlow.Culture, Strings.AutoAcceptToastBodyFormat, guestName, guestDevice));
        return true;
    }

    /// <summary>
    /// Writes the standing rule the host ticked in the request window (docs/ws-protocol.md section 5a) and turns the
    /// master switch on, since a host that has just said "accept from this guest from now on" has said what the switch
    /// says. Turning it on here is why the settings page shows the switch beside the list: the host must be able to
    /// find and undo in one place what it agreed to in another.
    /// </summary>
    private async Task GrantTrustAsync(IncomingRequest request, TrustGrant trust, CancellationToken ct)
    {
        if (request.GuestUserId == Guid.Empty || request.GuestDeviceId == Guid.Empty)
        {
            // A server older than section 5a: there is no identity to write a rule against, and writing one against
            // Guid.Empty would make it match every guest on that deployment.
            _logger.LogWarning(
                "Cannot trust {GuestName}: this server does not identify guests in request.incoming",
                request.GuestName);
            return;
        }

        var now = _time.GetUtcNow();
        var guest = new TrustedGuest(
            request.GuestUserId,
            request.GuestDeviceId,
            request.GuestName,
            request.GuestDevice,
            trust.MaxDurationMinutes,
            trust.For is { } window ? now + window : null,
            request.Allowlist.Version,
            now);

        try
        {
            var settings = _autoAccept.Current.WithoutExpired(now).With(guest) with { Enabled = true };
            await _autoAccept.SaveAsync(settings, ct).ConfigureAwait(false);
            _logger.LogInformation(
                "{GuestName} on {GuestDevice} is now trusted until {Expiry} for sessions up to {MaxMinutes} minutes",
                guest.GuestName,
                guest.GuestDevice,
                guest.ExpiresAt,
                guest.MaxDurationMinutes);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The session is already granted; only the standing rule is lost. Say so rather than letting the host
            // believe it will not be asked again.
            _logger.LogError(ex, "Could not save the trusted-guest rule for {GuestName}", guest.GuestName);
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
