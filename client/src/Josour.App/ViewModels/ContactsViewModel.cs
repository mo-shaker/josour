using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Josour.App.Services;
using Josour.App.Services.Notifications;
using Josour.Core.Control;
using Josour.Core.Session;
using Josour.Infrastructure.Api;
using Josour.Infrastructure.Contacts;
using Josour.Infrastructure.Session;
using Josour.Infrastructure.SystemProxy;

namespace Josour.App.ViewModels;

/// <summary>
/// The Contacts tab (ADR-0014): the people this user can share a connection with, and everything that happens between
/// them - inviting by email, answering invitations, using a contact's connection (<c>request.create</c>), offering one's
/// own (<c>offer.create</c>), answering an offer, letting a contact in without being asked, and removing a contact.
/// <para>
/// The lists come from <see cref="IContactsStore"/>, which the control channel keeps current; this view model only turns
/// them into rows and carries out what the user pressed.
/// </para>
/// </summary>
public sealed partial class ContactsViewModel : ObservableObject
{
    /// <summary>Offered session lengths; cut to <c>hello.ack settings.max_session_minutes</c>.</summary>
    public static readonly IReadOnlyList<int> OfferedDurations = new[] { 15, 30, 60, 120 };

    private const int DefaultMaxSessionMinutes = 120;

    private readonly IApiClient _api;
    private readonly IAuthSession _auth;
    private readonly IControlChannel _channel;
    private readonly SessionCoordinator _sessions;
    private readonly ControlChannelConnector _connector;
    private readonly IContactsStore _store;
    private readonly INotifier _notifier;
    private readonly ILogger<ContactsViewModel> _logger;
    private WaitingFor _waiting;

    private enum WaitingFor { Nothing, Request, Offer }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(InviteCommand))]
    private string _inviteEmail = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(InviteCommand))]
    private bool _isBusy;

    [ObservableProperty]
    private int _selectedDuration = 30;

    [ObservableProperty]
    private bool _isConnected;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CancelWaitingCommand))]
    private bool _isWaiting;

    [ObservableProperty]
    private string _waitingText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPendingOffer))]
    [NotifyCanExecuteChangedFor(nameof(AcceptOfferCommand))]
    [NotifyCanExecuteChangedFor(nameof(DeclineOfferCommand))]
    private OfferIncomingMessage? _pendingOffer;

    [ObservableProperty]
    private string _pendingOfferText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatusMessage))]
    private string _statusMessage = string.Empty;

    public ContactsViewModel(
        IApiClient api,
        IAuthSession auth,
        IControlChannel channel,
        SessionCoordinator sessions,
        ControlChannelConnector connector,
        IContactsStore store,
        INotifier notifier,
        ILogger<ContactsViewModel> logger)
    {
        _api = api;
        _auth = auth;
        _channel = channel;
        _sessions = sessions;
        _connector = connector;
        _store = store;
        _notifier = notifier;
        _logger = logger;

        Contacts.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasContacts));
            OnPropertyChanged(nameof(IsLoading));
            OnPropertyChanged(nameof(ShowsEmpty));
        };
        IncomingInvites.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasIncomingInvites));
        OutgoingInvites.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasOutgoingInvites));

        _store.Changed += () => UiThread.Post(() =>
        {
            IsLoaded = true;
            Rebuild();
        });
        _channel.MessageReceived += OnMessage;
        _channel.StateChanged += state => UiThread.Post(() =>
        {
            IsConnected = state == ControlChannelState.Connected;
            OnPropertyChanged(nameof(IsLoading));
            OnPropertyChanged(nameof(ShowsEmpty));
            if (IsConnected)
            {
                ApplyServerSettings(_connector.LastHello);
            }

            RefreshRowStates();
        });
        _sessions.PropertyChanged += OnSessionsPropertyChanged;
        _auth.Changed += (_, _) => UiThread.Post(() =>
        {
            if (!_auth.IsSignedIn)
            {
                _store.Clear();
                PendingOffer = null;
                SetStatus(string.Empty);
                IsLoaded = false;
            }
        });

        IsConnected = _channel.State == ControlChannelState.Connected;
        ApplyServerSettings(_connector.LastHello);
        Rebuild();
        IsLoaded = HasContacts;
    }

    public ObservableCollection<ContactItemViewModel> Contacts { get; } = new();

    public ObservableCollection<IncomingInviteItem> IncomingInvites { get; } = new();

    public ObservableCollection<OutgoingInviteItem> OutgoingInvites { get; } = new();

    /// <summary>Session lengths the user may pick, capped by the server's <c>max_session_minutes</c>.</summary>
    public ObservableCollection<int> DurationOptions { get; } = new(OfferedDurations);

    public bool HasContacts => Contacts.Count > 0;

    /// <summary>The first contact list has arrived (or there were contacts already): until then the tab shows loading rows.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLoading), nameof(ShowsEmpty))]
    private bool _isLoaded;

    /// <summary>Loading rows: signed in and connected, and the first list has not arrived. Offline shows what is known.</summary>
    public bool IsLoading => !IsLoaded && !HasContacts && IsConnected && _auth.IsSignedIn;

    /// <summary>"No contacts yet", only once the list is known to be empty.</summary>
    public bool ShowsEmpty => !HasContacts && !IsLoading;

    /// <summary>The feedback line is a failure (danger banner) rather than news (info banner).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasErrorMessage), nameof(HasInfoMessage))]
    private bool _statusIsError;

    public bool HasErrorMessage => HasStatusMessage && StatusIsError;

    public bool HasInfoMessage => HasStatusMessage && !StatusIsError;

    /// <summary>The contact whose "without asking" is waiting for the confirmation dialog; null when none is.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsConfirmingTrust), nameof(TrustConfirmTitle))]
    private ContactItemViewModel? _trustConfirmation;

    public bool IsConfirmingTrust => TrustConfirmation is not null;

    public string TrustConfirmTitle => TrustConfirmation is { } c
        ? string.Format(UiFlow.Culture, Strings.MobileTrustConfirmTitleFormat, c.DisplayName)
        : string.Empty;

    internal void AskTrust(ContactItemViewModel contact) => TrustConfirmation = contact;

    internal void TrustAnswered(ContactItemViewModel contact)
    {
        if (ReferenceEquals(TrustConfirmation, contact))
        {
            TrustConfirmation = null;
        }
    }

    [RelayCommand]
    private Task ConfirmTrustAsync() => TrustConfirmation?.ConfirmTrustCommand.ExecuteAsync(null) ?? Task.CompletedTask;

    [RelayCommand]
    private void CancelTrust() => TrustConfirmation?.CancelTrustCommand.Execute(null);

    /// <summary>
    /// Whether this machine can use somebody else's connection at all: only where Josour can point the operating system's
    /// proxy at its local proxy (Windows and macOS, ADR-0015). Elsewhere the guest side is refused outright.
    /// </summary>
    public static bool IsGuestRoleSupported => SystemProxyControllers.SupportedOnThisPlatform;

    /// <summary>The inverse, for the warning bar.</summary>
    public static bool IsGuestRoleUnavailable => !IsGuestRoleSupported;

    public bool HasIncomingInvites => IncomingInvites.Count > 0;

    public bool HasOutgoingInvites => OutgoingInvites.Count > 0;

    public bool HasPendingOffer => PendingOffer is not null;

    public bool HasStatusMessage => StatusMessage.Length > 0;

    /// <summary>A request or an offer could be started now (connected, and nothing else is pending or running).</summary>
    /// <summary>Idle, or Ended: a finished session's summary does not hold up the next one (it is replaced by it).</summary>
    public bool CanStartSession => IsConnected && !IsWaiting && !HasPendingOffer && _sessions.Phase is (SessionPhase.Idle or SessionPhase.Ended);

    // ---------------------------------------------------------------- invitations

    private bool CanInvite() => !IsBusy && InviteEmail.Contains('@', StringComparison.Ordinal);

    [RelayCommand(CanExecute = nameof(CanInvite))]
    private async Task InviteAsync(CancellationToken ct)
    {
        var email = InviteEmail.Trim();
        await RunAsync(
            async () =>
            {
                await _api.InviteAsync(email, ct).ConfigureAwait(true);
                InviteEmail = string.Empty;
                SetStatus(string.Format(UiFlow.Culture, Strings.ContactsInviteSentFormat, UiFlow.Ltr(email)));
                await _store.RefreshAsync(ct).ConfigureAwait(true);
            },
            Strings.ContactsInviteFailedFormat).ConfigureAwait(true);
    }

    internal Task AcceptInviteAsync(IncomingInviteItem invite) => RunAsync(
        async () =>
        {
            await _api.AcceptInviteAsync(invite.Id, CancellationToken.None).ConfigureAwait(true);
            await _store.RefreshAsync(CancellationToken.None).ConfigureAwait(true);
        });

    internal Task DeclineInviteAsync(IncomingInviteItem invite) => RunAsync(
        async () =>
        {
            await _api.DeclineInviteAsync(invite.Id, CancellationToken.None).ConfigureAwait(true);
            await _store.RefreshAsync(CancellationToken.None).ConfigureAwait(true);
        });

    internal Task WithdrawInviteAsync(OutgoingInviteItem invite) => RunAsync(
        async () =>
        {
            await _api.CancelInviteAsync(invite.Id, CancellationToken.None).ConfigureAwait(true);
            await _store.RefreshAsync(CancellationToken.None).ConfigureAwait(true);
        });

    // ---------------------------------------------------------------- per contact

    internal async Task UseConnectionAsync(ContactItemViewModel contact)
    {
        // A device on this machine's own network would not change the address sites see: any other one first.
        var host = _store.HostsOf(contact.UserId).OrderBy(h => h.SamePublicIp).FirstOrDefault();
        if (host is null || !CanStartSession)
        {
            return;
        }

        var duration = SelectedDuration;
        _waiting = WaitingFor.Request;
        WaitingText = string.Format(UiFlow.Culture, Strings.GuestWaitingTextFormat, contact.DisplayName);
        SetStatus(string.Empty);
        _logger.LogInformation("Contacts: asking {Contact} for {Minutes} min on {Device}", contact.DisplayName, duration, host.DeviceName);
        var error = await _sessions.RequestSessionAsync(host.DeviceId, duration, CancellationToken.None).ConfigureAwait(true);
        if (error is not null)
        {
            _waiting = WaitingFor.Nothing;
            SetStatus(DescribeError(error), isError: true);
        }
    }

    internal async Task OfferConnectionAsync(ContactItemViewModel contact)
    {
        if (!CanStartSession)
        {
            return;
        }

        var duration = SelectedDuration;
        _waiting = WaitingFor.Offer;
        WaitingText = string.Format(UiFlow.Culture, Strings.ContactsOfferWaitingFormat, contact.DisplayName);
        SetStatus(string.Empty);
        _logger.LogInformation("Contacts: offering {Minutes} min to {Contact}", duration, contact.DisplayName);
        var error = await _sessions.OfferSessionAsync(contact.UserId, duration, CancellationToken.None).ConfigureAwait(true);
        if (error is not null)
        {
            _waiting = WaitingFor.Nothing;
            SetStatus(DescribeError(error), isError: true);
        }
    }

    internal Task SetTrustAsync(ContactItemViewModel contact, bool allow) => RunAsync(
        async () =>
        {
            await _api.SetTrustAsync(contact.UserId, allow, CancellationToken.None).ConfigureAwait(true);
            await _store.RefreshAsync(CancellationToken.None).ConfigureAwait(true);
        },
        onFailure: Rebuild); // put the checkbox back to what the server holds

    internal Task RemoveAsync(ContactItemViewModel contact) => RunAsync(
        async () =>
        {
            await _api.RemoveContactAsync(contact.UserId, CancellationToken.None).ConfigureAwait(true);
            await _store.RefreshAsync(CancellationToken.None).ConfigureAwait(true);
        });

    // ---------------------------------------------------------------- waiting, offers

    private bool CanCancelWaiting() => IsWaiting;

    [RelayCommand(CanExecute = nameof(CanCancelWaiting))]
    private async Task CancelWaitingAsync(CancellationToken ct)
    {
        try
        {
            if (_waiting == WaitingFor.Offer)
            {
                await _sessions.CancelOfferAsync(ct).ConfigureAwait(true);
            }
            else
            {
                await _sessions.CancelRequestAsync(ct).ConfigureAwait(true);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Contacts: could not withdraw");
            SetStatus(Strings.GuestErrorNotConnected, isError: true);
        }
    }

    private bool CanAnswerOffer() => PendingOffer is not null;

    private bool CanAcceptOffer() => PendingOffer is not null && IsGuestRoleSupported;

    [RelayCommand(CanExecute = nameof(CanAcceptOffer))]
    private async Task AcceptOfferAsync(CancellationToken ct)
    {
        if (PendingOffer is not { } offer)
        {
            return;
        }

        PendingOffer = null;
        try
        {
            await _sessions.AcceptOfferAsync(offer.OfferId, ct).ConfigureAwait(true);
        }
        catch (InvalidOperationException ex) when (ex.Message == SessionCoordinator.NotPendingMessage)
        {
            // Not sent: this device is not the one the offer is waiting on any more (taken elsewhere, withdrawn, busy here).
            _logger.LogWarning("Contacts: offer.accept for {OfferId} not sent: the offer is no longer pending here", offer.OfferId);
            SetStatus(Strings.OfferNoLongerAvailable, isError: true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Contacts: offer.accept failed");
            SetStatus(Strings.GuestErrorNotConnected, isError: true);
        }
    }

    [RelayCommand(CanExecute = nameof(CanAnswerOffer))]
    private async Task DeclineOfferAsync(CancellationToken ct)
    {
        if (PendingOffer is not { } offer)
        {
            return;
        }

        PendingOffer = null;
        try
        {
            await _sessions.RejectOfferAsync(offer.OfferId, ct).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Contacts: offer.reject failed");
        }
    }

    private void OnMessage(ControlMessage message)
    {
        switch (message)
        {
            case HelloAckMessage ack:
                UiThread.Post(() => ApplyServerSettings(ack));
                break;

            case OfferIncomingMessage offer:
                UiThread.Post(() =>
                {
                    if (_sessions.Phase is not (SessionPhase.Idle or SessionPhase.RequestPending or SessionPhase.Ended) || IsWaiting)
                    {
                        return; // busy: the server will expire it, and the offering side is told
                    }

                    PendingOffer = offer;
                    PendingOfferText = string.Format(UiFlow.Culture, Strings.OfferIncomingFormat, offer.HostName, offer.DurationMin);
                    _notifier.ShowInfo(Strings.OfferToastTitle, string.Format(UiFlow.Culture, Strings.OfferToastBodyFormat, offer.HostName));
                });
                break;

            case OfferClosedMessage closed:
                UiThread.Post(() =>
                {
                    if (PendingOffer?.OfferId == closed.OfferId)
                    {
                        PendingOffer = null;
                    }
                });
                break;

            case OfferResultMessage { Accepted: false } result:
                UiThread.Post(() => SetStatus(result.Reason switch
                {
                    "expired" => Strings.OfferResultExpired,
                    "cancelled" => Strings.OfferResultCancelled,
                    "contact_removed" => Strings.ErrorNotContact,
                    _ => Strings.OfferResultRejected,
                }));
                break;

            case RequestResultMessage { Accepted: false } result when _waiting == WaitingFor.Request:
                UiThread.Post(() => SetStatus(result.Reason switch
                {
                    "expired" => Strings.GuestRequestExpiredText,
                    "cancelled" => Strings.GuestRequestCancelled,
                    "host_unavailable" => Strings.GuestErrorHostUnavailable,
                    "contact_removed" => Strings.ErrorNotContact,
                    _ => Strings.GuestRequestRejected,
                }));
                break;
        }
    }

    private void OnSessionsPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(SessionCoordinator.Phase))
        {
            return;
        }

        var phase = _sessions.Phase;
        UiThread.Post(() =>
        {
            if (phase != SessionPhase.RequestPending)
            {
                if (_waiting != WaitingFor.Nothing && phase == SessionPhase.Preparing)
                {
                    SetStatus(Strings.GuestRequestAccepted);
                }

                _waiting = WaitingFor.Nothing;
            }

            if (phase is not (SessionPhase.Idle or SessionPhase.RequestPending))
            {
                PendingOffer = null;
            }

            IsWaiting = phase == SessionPhase.RequestPending && _waiting != WaitingFor.Nothing;
            RefreshRowStates();
        });
    }

    // ---------------------------------------------------------------- lists

    private void Rebuild()
    {
        var contacts = _store.Contacts;
        Contacts.Clear();
        foreach (var contact in contacts)
        {
            Contacts.Add(new ContactItemViewModel(this, contact, _store));
        }

        var invites = _store.Invites;
        IncomingInvites.Clear();
        foreach (var invite in invites.Incoming)
        {
            IncomingInvites.Add(new IncomingInviteItem(this, invite));
        }

        OutgoingInvites.Clear();
        foreach (var invite in invites.Outgoing)
        {
            OutgoingInvites.Add(new OutgoingInviteItem(this, invite));
        }
    }

    private void RefreshRowStates()
    {
        OnPropertyChanged(nameof(CanStartSession));
        foreach (var contact in Contacts)
        {
            contact.RefreshState();
        }
    }

    partial void OnIsWaitingChanged(bool value) => RefreshRowStates();

    partial void OnPendingOfferChanged(OfferIncomingMessage? value) => RefreshRowStates();

    /// <summary>Caps <see cref="DurationOptions"/> with the server's <c>settings.max_session_minutes</c> (UI thread).</summary>
    public void ApplyServerSettings(HelloAckMessage? ack)
    {
        var max = ack?.Settings.MaxSessionMinutes ?? DefaultMaxSessionMinutes;
        var allowed = OfferedDurations.Where(d => d <= max).ToList();
        if (allowed.Count == 0)
        {
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

    internal static string DescribeError(string code) => code switch
    {
        ControlErrorCodes.HostUnavailable => Strings.GuestErrorHostUnavailable,
        ControlErrorCodes.SessionExists => Strings.GuestErrorSessionExists,
        ControlErrorCodes.RequestPending or SessionCoordinator.BusyCode => Strings.GuestErrorRequestPending,
        ControlErrorCodes.RateLimited => Strings.GuestErrorRateLimited,
        ControlErrorCodes.NotContact => Strings.ErrorNotContact,
        ControlErrorCodes.GuestUnavailable => Strings.OfferErrorGuestUnavailable,
        SessionCoordinator.NotConnectedCode => Strings.GuestErrorNotConnected,
        SessionCoordinator.TimeoutCode => Strings.GuestRequestDisconnected,
        _ => string.Format(UiFlow.Culture, Strings.GuestErrorGenericFormat, code),
    };

    private async Task RunAsync(Func<Task> action, string? failureFormat = null, Action? onFailure = null)
    {
        IsBusy = true;
        try
        {
            await action().ConfigureAwait(true);
        }
        catch (ApiException ex)
        {
            _logger.LogWarning("Contacts: {Code} {Message}", ex.Code, ex.Message);
            SetStatus(string.Format(UiFlow.Culture, failureFormat ?? Strings.ContactActionFailedFormat, ex.Message), isError: true);
            onFailure?.Invoke();
        }
        catch (ApiUnavailableException ex)
        {
            _logger.LogWarning("Contacts: server unavailable: {Message}", ex.Message);
            SetStatus(Strings.GuestErrorNotConnected, isError: true);
            onFailure?.Invoke();
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void SetStatus(string message, bool isError = false)
    {
        StatusIsError = isError;
        StatusMessage = message;
    }
}

/// <summary>One contact's row on the Contacts tab.</summary>
public sealed partial class ContactItemViewModel : ObservableObject
{
    private readonly ContactsViewModel _owner;
    private readonly IContactsStore _store;
    private bool _applying;

    [ObservableProperty]
    private bool _allowWithoutAsking;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TrustHelpText))]
    private bool _isConfirmingRemove;

    /// <summary>Turning "without asking" on is waiting for the confirmation dialog.</summary>
    [ObservableProperty]
    private bool _isConfirmingTrust;

    public ContactItemViewModel(ContactsViewModel owner, ContactDto contact, IContactsStore store)
    {
        _owner = owner;
        _store = store;
        Contact = contact;
        _applying = true;
        AllowWithoutAsking = contact.ITrust;
        _applying = false;
    }

    public ContactDto Contact { get; }

    public Guid UserId => Contact.UserId;

    public string DisplayName => Contact.DisplayName;

    /// <summary>The address, a technical value: kept left to right in the mirrored window.</summary>
    public string EmailText => UiFlow.Ltr(Contact.Email);

    public bool IsOnline => _store.IsOnline(UserId);

    /// <summary>One of the contact's devices is offering its connection right now.</summary>
    public bool IsSharing => _store.HostsOf(UserId).Count > 0;

    /// <summary>Every device the contact is sharing is on this machine's network: using it would not change the address.</summary>
    public bool IsOnSameNetwork => _store.HostsOf(UserId) is { Count: > 0 } hosts && hosts.All(h => h.SamePublicIp);

    /// <summary>For the dot: green while online, grey (unknown) while offline - offline is not an error.</summary>
    public bool? PresenceDot => IsOnline ? true : null;

    public string PresenceText => IsSharing ? Strings.ContactSharing : IsOnline ? Strings.ContactOnline : Strings.ContactOffline;

    public bool TrustsMe => Contact.TrustsMe;

    public bool CanUseConnection => ContactsViewModel.IsGuestRoleSupported && IsSharing && _owner.CanStartSession;

    public bool CanOfferConnection => IsOnline && _owner.CanStartSession;

    public void RefreshState()
    {
        OnPropertyChanged(nameof(IsOnline));
        OnPropertyChanged(nameof(PresenceDot));
        OnPropertyChanged(nameof(IsSharing));
        OnPropertyChanged(nameof(IsOnSameNetwork));
        OnPropertyChanged(nameof(PresenceText));
        OnPropertyChanged(nameof(CanUseConnection));
        OnPropertyChanged(nameof(CanOfferConnection));
        UseConnectionCommand.NotifyCanExecuteChanged();
        OfferConnectionCommand.NotifyCanExecuteChanged();
    }

    /// <summary>"On: accepted without asking…" or "Off: you are asked every time."</summary>
    public string TrustHelpText => AllowWithoutAsking ? Strings.MobileTrustOnHelp : Strings.MobileTrustOffHelp;

    /// <summary>"Remove Sara? They will no longer be able to ask for your connection."</summary>
    public string RemoveConfirmText => string.Format(UiFlow.Culture, Strings.MobileRemoveConfirmFormat, DisplayName);

    /// <summary>The avatar's letter: the first whole grapheme of the name (an Arabic letter with its marks).</summary>
    public string Initial => InitialOf(DisplayName);

    /// <summary>The switch's accessible name: the setting and whose it is.</summary>
    public string TrustAccessibleName => Strings.ContactAllowAlways + " — " + DisplayName;

    public static string InitialOf(string? name)
    {
        var trimmed = (name ?? string.Empty).Trim();
        return trimmed.Length == 0 ? "?" : System.Globalization.StringInfo.GetNextTextElement(trimmed, 0).ToUpperInvariant();
    }

    /// <summary>
    /// Turning it on asks first (Phase 1 decision 10): a standing consent is not given by one possibly accidental click.
    /// Turning it off takes effect at once, as it always did.
    /// </summary>
    partial void OnAllowWithoutAskingChanged(bool value)
    {
        OnPropertyChanged(nameof(TrustHelpText));
        if (_applying)
        {
            return;
        }

        if (value)
        {
            IsConfirmingTrust = true;
            _owner.AskTrust(this);
            return;
        }

        _ = _owner.SetTrustAsync(this, false);
    }

    [RelayCommand]
    private Task ConfirmTrustAsync()
    {
        IsConfirmingTrust = false;
        _owner.TrustAnswered(this);
        return _owner.SetTrustAsync(this, true);
    }

    [RelayCommand]
    private void CancelTrust()
    {
        IsConfirmingTrust = false;
        _owner.TrustAnswered(this);
        _applying = true;
        AllowWithoutAsking = false;
        _applying = false;
    }

    [RelayCommand]
    private void CancelRemove() => IsConfirmingRemove = false;

    [RelayCommand(CanExecute = nameof(CanUseConnection))]
    private Task UseConnectionAsync() => _owner.UseConnectionAsync(this);

    [RelayCommand(CanExecute = nameof(CanOfferConnection))]
    private Task OfferConnectionAsync() => _owner.OfferConnectionAsync(this);

    /// <summary>Two presses: the first asks "remove for good?", the second removes.</summary>
    [RelayCommand]
    private Task RemoveAsync()
    {
        if (!IsConfirmingRemove)
        {
            IsConfirmingRemove = true;
            return Task.CompletedTask;
        }

        return _owner.RemoveAsync(this);
    }
}

public sealed partial class IncomingInviteItem
{
    private readonly ContactsViewModel _owner;

    public IncomingInviteItem(ContactsViewModel owner, IncomingInviteDto invite)
    {
        _owner = owner;
        Invite = invite;
    }

    public IncomingInviteDto Invite { get; }

    public Guid Id => Invite.Id;

    public string Text => string.Format(UiFlow.Culture, Strings.ContactsIncomingFormat, Invite.FromDisplayName);

    public string Initial => ContactItemViewModel.InitialOf(Invite.FromDisplayName);

    public string EmailText => UiFlow.Ltr(Invite.FromEmail);

    [RelayCommand]
    private Task AcceptAsync() => _owner.AcceptInviteAsync(this);

    [RelayCommand]
    private Task DeclineAsync() => _owner.DeclineInviteAsync(this);
}

public sealed partial class OutgoingInviteItem
{
    private readonly ContactsViewModel _owner;

    public OutgoingInviteItem(ContactsViewModel owner, OutgoingInviteDto invite)
    {
        _owner = owner;
        Invite = invite;
    }

    public OutgoingInviteDto Invite { get; }

    public Guid Id => Invite.Id;

    public string EmailText => UiFlow.Ltr(Invite.Email);

    public string StatusText => Invite.IsExpired ? Strings.ContactsOutgoingExpired : Strings.ContactsOutgoingPending;

    [RelayCommand]
    private Task WithdrawAsync() => _owner.WithdrawInviteAsync(this);
}
