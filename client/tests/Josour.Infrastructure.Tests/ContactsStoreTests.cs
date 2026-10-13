using Josour.Core.Control;
using Josour.Infrastructure.Api;
using Josour.Infrastructure.Contacts;
using Josour.Infrastructure.Tests.Support;

namespace Josour.Infrastructure.Tests;

/// <summary><see cref="ContactsStore"/>: REST is the source of the lists, the control channel keeps them current (ADR-0014).</summary>
public sealed class ContactsStoreTests
{
    private static readonly DateTimeOffset T = new(2026, 10, 13, 9, 0, 0, TimeSpan.Zero);

    private static ContactDto Contact(string name, bool online = false, bool iTrust = false) =>
        new(Guid.NewGuid(), name, name.ToLowerInvariant() + "@example.com", online, iTrust, TrustsMe: false, T);

    private static async Task UntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++)
        {
            await Task.Delay(10);
        }

        Assert.True(condition());
    }

    [Fact]
    public async Task Refresh_ReadsContactsAndInvitations_AndSaysSo()
    {
        var api = new FakeApiClient();
        var sara = Contact("Sara", online: true, iTrust: true);
        api.Contacts.Add(sara);
        api.Invites = new InvitesDto(
            [new IncomingInviteDto(Guid.NewGuid(), Guid.NewGuid(), "Omar", "omar@example.com", T, T.AddDays(30))],
            []);
        using var store = new ContactsStore(api, new FakeControlChannel());
        var changes = 0;
        store.Changed += () => changes++;

        await store.RefreshAsync(CancellationToken.None);

        Assert.Equal([sara], store.Contacts);
        Assert.Single(store.Invites.Incoming);
        Assert.True(store.IsOnline(sara.UserId));
        Assert.True(store.Trusts(sara.UserId));
        Assert.False(store.Trusts(Guid.NewGuid()));
        Assert.Equal(1, changes);
    }

    [Fact]
    public async Task ContactsChangedAndHelloAck_TriggerARefresh()
    {
        var api = new FakeApiClient();
        var channel = new FakeControlChannel();
        using var store = new ContactsStore(api, channel);

        api.Contacts.Add(Contact("Sara"));
        channel.Publish(new ContactsChangedMessage());
        await UntilAsync(() => store.Contacts.Count == 1);

        api.Contacts.Add(Contact("Omar"));
        channel.Publish(new HelloAckMessage(T, "203.0.113.7", new ServerSettings(120, 60, [80, 443], false), 1));
        await UntilAsync(() => store.Contacts.Count == 2);
    }

    [Fact]
    public async Task Presence_FullReplaces_DeltaUpdates()
    {
        var api = new FakeApiClient();
        var sara = Contact("Sara", online: false);
        var omar = Contact("Omar", online: true);
        api.Contacts.AddRange([sara, omar]);
        var channel = new FakeControlChannel();
        using var store = new ContactsStore(api, channel);
        await store.RefreshAsync(CancellationToken.None);

        channel.Publish(new ContactsPresenceMessage(false, [new ContactPresenceDto(sara.UserId, true)]));
        Assert.True(store.IsOnline(sara.UserId));
        Assert.True(store.IsOnline(omar.UserId));

        channel.Publish(new ContactsPresenceMessage(true, [new ContactPresenceDto(sara.UserId, false)]));
        Assert.False(store.IsOnline(sara.UserId));
        Assert.False(store.IsOnline(omar.UserId)); // a full snapshot that leaves someone out says they are offline
    }

    [Fact]
    public void Hosts_AreGroupedByTheContactTheyBelongTo()
    {
        var channel = new FakeControlChannel();
        using var store = new ContactsStore(new FakeApiClient(), channel);
        var sara = Guid.NewGuid();

        channel.Publish(new HostsMessage("hosts.update",
        [
            new HostInfoDto(Guid.NewGuid(), "Sara", "SARA-PC", true, sara),
            new HostInfoDto(Guid.NewGuid(), "Sara", "SARA-LAPTOP", null, sara),
            new HostInfoDto(Guid.NewGuid(), "Old server", "NO-USER-ID", null), // a server that predates contacts
        ]));

        Assert.Equal(2, store.HostsOf(sara).Count);
        Assert.Empty(store.HostsOf(Guid.NewGuid()));

        channel.Publish(new HostsMessage("hosts.update", []));
        Assert.Empty(store.HostsOf(sara));
    }

    [Fact]
    public async Task Clear_ForgetsEverything()
    {
        var api = new FakeApiClient();
        api.Contacts.Add(Contact("Sara", online: true));
        using var store = new ContactsStore(api, new FakeControlChannel());
        await store.RefreshAsync(CancellationToken.None);

        store.Clear();

        Assert.Empty(store.Contacts);
        Assert.Empty(store.Invites.Incoming);
    }
}
