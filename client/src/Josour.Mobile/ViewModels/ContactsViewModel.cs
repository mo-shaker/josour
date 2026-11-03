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
    [NotifyPropertyChangedFor(nameof(HasStatusMessage))]
    private string _statusMessage = string.Empty;

    public ContactsViewModel(IApiClient api, IContactsStore store, IMobileNotifier notifier, ILogger<ContactsViewModel> logger)
    {
        _api = api;
        _store = store;
        _notifier = notifier;
        _logger = logger;
        Contacts.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasContacts));
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
        if (await RunAsync(() => _api.InviteAsync(email, CancellationToken.None), UiStringKeys.ContactsInviteFailedFormat))
        {
            InviteEmail = string.Empty;
            StatusMessage = Tr.Format(UiStringKeys.ContactsInviteSentFormat, Tr.Ltr(email));
        }
    }

    internal Task AcceptAsync(IncomingInviteDto invite) =>
        RunAsync(() => _api.AcceptInviteAsync(invite.Id, CancellationToken.None));

    internal Task DeclineAsync(IncomingInviteDto invite) =>
        RunAsync(() => _api.DeclineInviteAsync(invite.Id, CancellationToken.None));

    internal Task WithdrawAsync(OutgoingInviteDto invite) =>
        RunAsync(() => _api.CancelInviteAsync(invite.Id, CancellationToken.None));

    internal Task SetTrustAsync(ContactDto contact, bool allow) =>
        RunAsync(() => _api.SetTrustAsync(contact.UserId, allow, CancellationToken.None), onFailure: Rebuild);

    internal Task RemoveAsync(ContactDto contact) =>
        RunAsync(() => _api.RemoveContactAsync(contact.UserId, CancellationToken.None));

    /// <summary>One call, then the lists from the server; a failure is shown on the page, never thrown.</summary>
    private async Task<bool> RunAsync(Func<Task> action, string failureFormat = UiStringKeys.ContactActionFailedFormat, Action? onFailure = null)
    {
        IsBusy = true;
        StatusMessage = string.Empty;
        try
        {
            await action();
            await _store.RefreshAsync(CancellationToken.None);
            return true;
        }
        catch (ApiException ex)
        {
            _logger.LogWarning("Contacts: {Code} {Message}", ex.Code, ex.Message);
            StatusMessage = Tr.Format(failureFormat, ex.Message);
            onFailure?.Invoke();
            return false;
        }
        catch (ApiUnavailableException ex)
        {
            _logger.LogWarning("Contacts: server unavailable: {Message}", ex.Message);
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

/// <summary>A contact: online or not, "may use my connection without asking", and removing them (asked twice).</summary>
public sealed partial class ContactItem : ObservableObject
{
    private readonly ContactsViewModel _owner;
    private readonly bool _loading;

    [ObservableProperty]
    private bool _allowWithoutAsking;

    [ObservableProperty]
    private bool _isConfirmingRemove;

    public ContactItem(ContactsViewModel owner, ContactDto contact, bool online)
    {
        _owner = owner;
        Contact = contact;
        IsOnline = online;
        _loading = true;
        AllowWithoutAsking = contact.ITrust;
        _loading = false;
    }

    public ContactDto Contact { get; }

    public string DisplayName => Contact.DisplayName;

    public string EmailText => Tr.Ltr(Contact.Email);

    public bool IsOnline { get; }

    public string PresenceText => Tr.Get(IsOnline ? UiStringKeys.ContactOnline : UiStringKeys.ContactOffline);

    partial void OnAllowWithoutAskingChanged(bool value)
    {
        if (!_loading) _ = _owner.SetTrustAsync(Contact, value);
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
