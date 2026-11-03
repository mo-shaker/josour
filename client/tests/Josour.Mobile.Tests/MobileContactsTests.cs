using System.Reflection;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Josour.Core.Control;
using Josour.Infrastructure.Api;
using Josour.Infrastructure.Contacts;
using Josour.Mobile.ViewModels;
using Josour.Mobile.Views;
using Microsoft.Extensions.Logging.Abstractions;

namespace Josour.Mobile.Tests;

/// <summary>The phone's contacts page: invitations reach the user, in the app and as a notification, and can be answered.</summary>
public class MobileContactsTests
{
    private static readonly ContactDto Sara = new(Guid.NewGuid(), "Sara", "sara@example.com", true, false, false, DateTimeOffset.UtcNow);

    [AvaloniaFact]
    public void An_invitation_that_arrives_later_rings_once_and_is_cleared_when_it_goes()
    {
        var store = new FakeContactsStore();
        var already = Invite("Omar");
        store.SetInvites(already);
        var notifier = new FakeNotifier();
        var page = new ContactsViewModel(FakeApi.Create(out _), store, notifier, NullLogger<ContactsViewModel>.Instance);

        page.StartAnnouncing();
        var fresh = Invite("Mona");
        store.SetInvites(already, fresh);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(new[] { "invite:" + fresh.Id }, notifier.Events); // the one on screen at sign-in does not ring
        Assert.Equal(2, page.IncomingCount);
        Assert.True(page.HasIncomingInvites);

        store.SetInvites(already);
        Dispatcher.UIThread.RunJobs();
        Assert.Contains("uninvite:" + fresh.Id, notifier.Events);
        Assert.Equal(1, page.IncomingCount);
    }

    [AvaloniaFact]
    public void Nothing_rings_before_the_first_list_after_sign_in()
    {
        var store = new FakeContactsStore();
        var notifier = new FakeNotifier();
        _ = new ContactsViewModel(FakeApi.Create(out _), store, notifier, NullLogger<ContactsViewModel>.Instance);

        store.SetInvites(Invite("Omar"));
        Dispatcher.UIThread.RunJobs();

        Assert.Empty(notifier.Events);
    }

    [AvaloniaFact]
    public async Task Accepting_an_invitation_calls_the_server_and_reads_the_lists_again()
    {
        var store = new FakeContactsStore();
        var invite = Invite("Omar");
        store.SetInvites(invite);
        var page = new ContactsViewModel(FakeApi.Create(out var calls), store, new FakeNotifier(), NullLogger<ContactsViewModel>.Instance);
        Dispatcher.UIThread.RunJobs();

        await page.IncomingInvites.Single().AcceptCommand.ExecuteAsync(null);

        Assert.Contains($"AcceptInviteAsync:{invite.Id}", calls);
        Assert.Equal(1, store.Refreshes);
        Assert.False(page.HasStatusMessage);
    }

    [AvaloniaFact]
    public async Task A_refused_call_is_explained_on_the_page()
    {
        var store = new FakeContactsStore();
        var page = new ContactsViewModel(
            FakeApi.Create(out _, fail: new ApiException(System.Net.HttpStatusCode.NotFound, "not_found", "No such user")),
            store, new FakeNotifier(), NullLogger<ContactsViewModel>.Instance)
        {
            InviteEmail = "nobody@example.com",
        };

        await page.InviteCommand.ExecuteAsync(null);

        Assert.True(page.HasStatusMessage);
        Assert.Contains("No such user", page.StatusMessage);
        Assert.Equal("nobody@example.com", page.InviteEmail); // kept, to correct and send again
    }

    [AvaloniaFact]
    public async Task Removing_a_contact_asks_first()
    {
        var store = new FakeContactsStore { ContactsList = [Sara] };
        var page = new ContactsViewModel(FakeApi.Create(out var calls), store, new FakeNotifier(), NullLogger<ContactsViewModel>.Instance);
        var row = page.Contacts.Single();

        await row.RemoveCommand.ExecuteAsync(null);
        Assert.True(row.IsConfirmingRemove);
        Assert.Empty(calls);

        await row.RemoveCommand.ExecuteAsync(null);
        Assert.Contains($"RemoveContactAsync:{Sara.UserId}", calls);
    }

    [AvaloniaFact]
    public void The_contacts_page_loads_with_invitations_and_contacts()
    {
        using var phone = new TestPhone();
        var main = phone.Get<MainViewModel>();
        main.ShowContacts();
        var view = new MainView { DataContext = main };
        var window = new Avalonia.Controls.Window { Content = view, Width = 400, Height = 800 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        Assert.True(main.IsContactsPage);
        Assert.False(main.IsHostPage);
        window.Close();
    }

    private static IncomingInviteDto Invite(string from) =>
        new(Guid.NewGuid(), Guid.NewGuid(), from, from.ToLowerInvariant() + "@example.com", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(7));
}

internal sealed class FakeContactsStore : IContactsStore
{
    private InvitesDto _invites = InvitesDto.Empty;

    public IReadOnlyList<ContactDto> ContactsList { get; set; } = [];

    public int Refreshes { get; private set; }

    public IReadOnlyList<ContactDto> Contacts => ContactsList;

    public InvitesDto Invites => _invites;

    public event Action? Changed;

    public void SetInvites(params IncomingInviteDto[] incoming)
    {
        _invites = new InvitesDto(incoming, []);
        Changed?.Invoke();
    }

    public bool IsOnline(Guid userId) => ContactsList.Any(c => c.UserId == userId && c.Online);

    public IReadOnlyList<HostInfoDto> HostsOf(Guid userId) => [];

    public bool Trusts(Guid userId) => false;

    public Task RefreshAsync(CancellationToken ct)
    {
        Refreshes++;
        Changed?.Invoke();
        return Task.CompletedTask;
    }

    public void Clear() => _invites = InvitesDto.Empty;
}

/// <summary>An <see cref="IApiClient"/> that records the contact calls and answers each with a default (or one failure).</summary>
public class FakeApi : DispatchProxy
{
    private List<string> _calls = null!;
    private Exception? _fail;

    public static IApiClient Create(out List<string> calls, Exception? fail = null)
    {
        var api = Create<IApiClient, FakeApi>();
        var proxy = (FakeApi)(object)api;
        proxy._calls = calls = new List<string>();
        proxy._fail = fail;
        return api;
    }

    protected override object? Invoke(MethodInfo? method, object?[]? args)
    {
        _calls.Add($"{method!.Name}:{args?.FirstOrDefault()}");
        var type = method.ReturnType;
        if (_fail is not null)
        {
            if (type == typeof(Task)) return Task.FromException(_fail);
            var fromException = typeof(Task).GetMethod(nameof(Task.FromException), 1, [typeof(Exception)])!.MakeGenericMethod(type.GetGenericArguments()[0]);
            return fromException.Invoke(null, [_fail]);
        }

        if (type == typeof(Task)) return Task.CompletedTask;
        var result = typeof(Task).GetMethod(nameof(Task.FromResult))!.MakeGenericMethod(type.GetGenericArguments()[0]);
        return result.Invoke(null, [null]);
    }
}
