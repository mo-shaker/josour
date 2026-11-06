using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Josour.Infrastructure.Api;
using Josour.Infrastructure.Contacts;
using Josour.Infrastructure.Localization;
using Josour.Mobile.Platform;
using Microsoft.Extensions.Logging;

namespace Josour.Mobile.ViewModels;

/// <summary>
/// The phone's contacts page (ADR-0014): invitations waiting for an answer first, then the people who may ask to use this
/// phone's connection (with "without asking" per contact), then inviting someone by email. The phone only hosts, so there is
/// no "use their connection" here. The lists are <see cref="IContactsStore"/>'s, which <c>contacts.changed</c> keeps current;
/// an invitation that appears while the user is elsewhere is also announced with a notification.
/// </summary>
public sealed partial class ContactsViewModel : ObservableObject
{
    private readonly IApiClient _api;
    private readonly IContactsStore _store;
    private readonly IMobileNotifier _notifier;
    private readonly ILogger<ContactsViewModel> _logger;
    private readonly HashSet<Guid> _knownInvites = new();
    private bool _announcing;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(InviteCommand))]
    private string _inviteEmail = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(InviteCommand))]
    private bool _isBusy;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatusMessage), nameof(HasErrorMessage), nameof(HasSuccessMessage), nameof(HasPageError), nameof(HasInviteError), nameof(HasInviteSuccess))]
    private string _statusMessage = string.Empty;

    /// <summary>The last action failed: its message shows as a problem, not as news.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasErrorMessage), nameof(HasSuccessMessage), nameof(HasPageError), nameof(HasInviteError), nameof(HasInviteSuccess))]
    private bool _statusIsError;

    /// <summary>The message is about inviting: it shows in the invite card instead of at the top of the page.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPageError), nameof(HasInviteError), nameof(HasInviteSuccess))]
    private bool _statusFromInvite;

    /// <summary>The first list after sign-in has arrived: until then the page shows placeholders, never "No contacts yet".</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsEmpty), nameof(IsLoading))]
    private bool _isLoaded;

    /// <summary>The contact whose "without asking" is waiting for the confirmation sheet.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTrustCandidate))]
    private ContactItem? _trustCandidate;

    public ContactsViewModel(IApiClient api, IContactsStore store, IMobileNotifier notifier, ILogger<ContactsViewModel> logger)
    {
        _api = api;
        _store = store;
        _notifier = notifier;
        _logger = logger;
        Contacts.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasContacts));
            OnPropertyChanged(nameof(ShowsEmpty));
        };
        OutgoingInvites.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasOutgoingInvites));
        IncomingInvites.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasIncomingInvites));
            OnPropertyChanged(nameof(IncomingCount));
            OnPropertyChanged(nameof(WaitingText));
        };
        _store.Changed += () => Dispatcher.UIThread.Post(Rebuild);
        Rebuild();
    }

    public ObservableCollection<IncomingInviteItem> IncomingInvites { get; } = new();

    public ObservableCollection<OutgoingInviteItem> OutgoingInvites { get; } = new();

    public ObservableCollection<ContactItem> Contacts { get; } = new();

    public bool HasIncomingInvites => IncomingInvites.Count > 0;

    public bool HasOutgoingInvites => OutgoingInvites.Count > 0;

    public bool HasContacts => Contacts.Count > 0;

    public bool HasStatusMessage => StatusMessage.Length > 0;

    public bool HasErrorMessage => HasStatusMessage && StatusIsError;

    public bool HasSuccessMessage => HasStatusMessage && !StatusIsError;

    public bool HasPageError => HasErrorMessage && !StatusFromInvite;

    public bool HasInviteError => HasErrorMessage && StatusFromInvite;

    public bool HasInviteSuccess => HasSuccessMessage && StatusFromInvite;

    public bool IsLoading => !IsLoaded;

    public bool ShowsEmpty => IsLoaded && !HasContacts;

    public bool HasTrustCandidate => TrustCandidate is not null;

    /// <summary>For the badge on the contacts tab.</summary>
    public int IncomingCount => IncomingInvites.Count;

    /// <summary>The banner on the host page while invitations wait.</summary>
    public string WaitingText => Tr.Format(UiStringKeys.MobileInvitationsWaitingFormat, IncomingInvites.Count);

    /// <summary>
    /// From now on an invitation that was not there before is announced with a notification. Called once the first list
    /// after sign-in is in, so invitations the user can already see on opening the app do not ring as well.
    /// </summary>
    public void StartAnnouncing()
    {
        lock (_knownInvites)
        {
            _knownInvites.Clear();
            _knownInvites.UnionWith(_store.Invites.Incoming.Select(i => i.Id));
            _announcing = true;
        }

        IsLoaded = true;
    }

    /// <summary>Sign-out: nothing is announced until the next sign-in has its first list.</summary>
    public void Reset()
    {
        lock (_knownInvites)
        {
            foreach (var id in _knownInvites) _notifier.CancelInvitation(id);
            _knownInvites.Clear();
            _announcing = false;
        }

        StatusMessage = string.Empty;
        InviteEmail = string.Empty;
        TrustCandidate = null;
        IsLoaded = false;
    }

    /// <summary>The texts again, in the language just switched to.</summary>
    public void RefreshTexts()
    {
        StatusMessage = string.Empty;
        Rebuild();
        OnPropertyChanged(nameof(WaitingText));
    }

    private void Rebuild()
    {
        var invites = _store.Invites;
        AnnounceNewInvitations(invites.Incoming);
        TrustCandidate = null; // its item is about to be replaced

        Replace(IncomingInvites, invites.Incoming.Select(i => new IncomingInviteItem(this, i)));
        Replace(OutgoingInvites, invites.Outgoing.Select(i => new OutgoingInviteItem(this, i)));
        Replace(Contacts, _store.Contacts
            .OrderByDescending(c => _store.IsOnline(c.UserId))
            .ThenBy(c => c.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .Select(c => new ContactItem(this, c, _store.IsOnline(c.UserId))));
    }

    private void AnnounceNewInvitations(IReadOnlyList<IncomingInviteDto> incoming)
    {
        lock (_knownInvites)
        {
            if (!_announcing) return;

            foreach (var invite in incoming.Where(i => !_knownInvites.Contains(i.Id)))
            {
                _logger.LogInformation("Contact invitation {InviteId} from {Name}", invite.Id, invite.FromDisplayName);
                _notifier.ShowInvitation(
                    invite.Id,
                    Tr.Get(UiStringKeys.MobileInvitationTitle),
                    Tr.Format(UiStringKeys.ContactsIncomingFormat, invite.FromDisplayName));
            }

            var current = incoming.Select(i => i.Id).ToHashSet();
            foreach (var gone in _knownInvites.Where(id => !current.Contains(id)))
            {
                _notifier.CancelInvitation(gone);
            }

            _knownInvites.Clear();
            _knownInvites.UnionWith(current);
        }
    }

    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> items)
    {
        target.Clear();
        foreach (var item in items) target.Add(item);
    }

    // ---------------------------------------------------------------- actions

    private bool CanInvite() => !IsBusy && InviteEmail.Trim() is { Length: > 2 } email && email.Contains('@', StringComparison.Ordinal);

    [RelayCommand(CanExecute = nameof(CanInvite))]
    private async Task InviteAsync()
    {
        var email = InviteEmail.Trim();
        StatusFromInvite = true;
        if (await RunAsync(() => _api.InviteAsync(email, CancellationToken.None), UiStringKeys.ContactsInviteFailedFormat))
        {
            InviteEmail = string.Empty;
            StatusIsError = false;
            StatusMessage = Tr.Format(UiStringKeys.ContactsInviteSentFormat, Tr.Ltr(email));
        }
    }

    internal Task AcceptAsync(IncomingInviteDto invite) =>
        RunOnPageAsync(() => _api.AcceptInviteAsync(invite.Id, CancellationToken.None));

    internal Task DeclineAsync(IncomingInviteDto invite) =>
        RunOnPageAsync(() => _api.DeclineInviteAsync(invite.Id, CancellationToken.None));

    internal Task WithdrawAsync(OutgoingInviteDto invite) =>
        RunOnPageAsync(() => _api.CancelInviteAsync(invite.Id, CancellationToken.None));

    /// <summary>Turning "without asking" on waits for the sheet; <see cref="ContactItem.ConfirmTrustCommand"/> answers it.</summary>
    internal void AskTrust(ContactItem item) => TrustCandidate = item;

    internal void TrustAnswered(ContactItem item)
    {
        if (ReferenceEquals(TrustCandidate, item)) TrustCandidate = null;
    }

    internal Task SetTrustAsync(ContactDto contact, bool allow) =>
        RunOnPageAsync(() => _api.SetTrustAsync(contact.UserId, allow, CancellationToken.None), onFailure: Rebuild);

    internal Task RemoveAsync(ContactDto contact) =>
        RunOnPageAsync(() => _api.RemoveContactAsync(contact.UserId, CancellationToken.None));

    private Task<bool> RunOnPageAsync(Func<Task> action, string failureFormat = UiStringKeys.ContactActionFailedFormat, Action? onFailure = null)
    {
        StatusFromInvite = false;
        return RunAsync(action, failureFormat, onFailure);
    }

    /// <summary>One call, then the lists from the server; a failure is shown on the page, never thrown.</summary>
    private async Task<bool> RunAsync(Func<Task> action, string failureFormat = UiStringKeys.ContactActionFailedFormat, Action? onFailure = null)
    {
        IsBusy = true;
        StatusMessage = string.Empty;
        StatusIsError = false;
        try
        {
            await action();
            await _store.RefreshAsync(CancellationToken.None);
            return true;
        }
        catch (ApiException ex)
        {
            _logger.LogWarning("Contacts: {Code} {Message}", ex.Code, ex.Message);
            StatusIsError = true;
            StatusMessage = Tr.Format(failureFormat, ex.Message);
            onFailure?.Invoke();
            return false;
        }
        catch (ApiUnavailableException ex)
        {
            _logger.LogWarning("Contacts: server unavailable: {Message}", ex.Message);
            StatusIsError = true;
            StatusMessage = Tr.Get(UiStringKeys.GuestErrorNotConnected);
            onFailure?.Invoke();
            return false;
        }
        finally
        {
            IsBusy = false;
        }
    }
}

/// <summary>An invitation to this user: who, and Accept / Decline.</summary>
public sealed partial class IncomingInviteItem(ContactsViewModel owner, IncomingInviteDto invite) : ObservableObject
{
    public Guid Id => invite.Id;

    public string Title => Tr.Format(UiStringKeys.ContactsIncomingFormat, invite.FromDisplayName);

    public string EmailText => Tr.Ltr(invite.FromEmail);

    public string Initial => ContactItem.InitialOf(invite.FromDisplayName);

    [RelayCommand]
    private Task AcceptAsync() => owner.AcceptAsync(invite);

    [RelayCommand]
    private Task DeclineAsync() => owner.DeclineAsync(invite);
}

/// <summary>An invitation this user sent and nobody answered yet (or that expired).</summary>
public sealed partial class OutgoingInviteItem(ContactsViewModel owner, OutgoingInviteDto invite) : ObservableObject
{
    public string EmailText => Tr.Ltr(invite.Email);

    public string StatusText => Tr.Get(invite.IsExpired ? UiStringKeys.ContactsOutgoingExpired : UiStringKeys.ContactsOutgoingPending);

    [RelayCommand]
    private Task WithdrawAsync() => owner.WithdrawAsync(invite);
}

/// <summary>
/// A contact: online or not, "may use my connection without asking", and removing them (asked twice). Turning "without
/// asking" on is a standing consent, so it waits for the confirmation sheet; turning it off takes effect at once.
/// </summary>
public sealed partial class ContactItem : ObservableObject
{
    private readonly ContactsViewModel _owner;
    private bool _quiet;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TrustHelpText))]
    private bool _allowWithoutAsking;

    [ObservableProperty]
    private bool _isConfirmingRemove;

    [ObservableProperty]
    private bool _isConfirmingTrust;

    public ContactItem(ContactsViewModel owner, ContactDto contact, bool online)
    {
        _owner = owner;
        Contact = contact;
        IsOnline = online;
        _quiet = true;
        AllowWithoutAsking = contact.ITrust;
        _quiet = false;
    }

    public ContactDto Contact { get; }

    public string DisplayName => Contact.DisplayName;

    public string Initial => InitialOf(Contact.DisplayName);

    public string EmailText => Tr.Ltr(Contact.Email);

    public bool IsOnline { get; }

    public bool IsOffline => !IsOnline;

    public string PresenceText => Tr.Get(IsOnline ? UiStringKeys.ContactOnline : UiStringKeys.ContactOffline);

    public string TrustHelpText => Tr.Get(AllowWithoutAsking ? UiStringKeys.MobileTrustOnHelp : UiStringKeys.MobileTrustOffHelp);

    public string TrustConfirmTitle => Tr.Format(UiStringKeys.MobileTrustConfirmTitleFormat, DisplayName);

    public string RemoveConfirmText => Tr.Format(UiStringKeys.MobileRemoveConfirmFormat, DisplayName);

    /// <summary>The first letter of a name, as a whole grapheme (an Arabic letter with its marks), for the avatar.</summary>
    public static string InitialOf(string? name)
    {
        var trimmed = (name ?? string.Empty).Trim();
        return trimmed.Length == 0 ? "?" : System.Globalization.StringInfo.GetNextTextElement(trimmed, 0).ToUpperInvariant();
    }

    partial void OnAllowWithoutAskingChanged(bool value)
    {
        if (_quiet) return;
        if (value)
        {
            IsConfirmingTrust = true;
            _owner.AskTrust(this);
            return;
        }

        _ = _owner.SetTrustAsync(Contact, false);
    }

    [RelayCommand]
    private Task ConfirmTrustAsync()
    {
        IsConfirmingTrust = false;
        _owner.TrustAnswered(this);
        return _owner.SetTrustAsync(Contact, true);
    }

    [RelayCommand]
    private void CancelTrust()
    {
        IsConfirmingTrust = false;
        _owner.TrustAnswered(this);
        _quiet = true;
        AllowWithoutAsking = false;
        _quiet = false;
    }

    [RelayCommand]
    private Task RemoveAsync()
    {
        if (!IsConfirmingRemove)
        {
            IsConfirmingRemove = true;
            return Task.CompletedTask;
        }

        IsConfirmingRemove = false;
        return _owner.RemoveAsync(Contact);
    }

    [RelayCommand]
    private void KeepContact() => IsConfirmingRemove = false;
}
