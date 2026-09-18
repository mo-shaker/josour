using Josour.Core.Session;
using Josour.Infrastructure.Settings;

namespace Josour.Infrastructure.Tests;

/// <summary>
/// docs/ws-protocol.md section 5a. This file is a consent record, so every test here is really one question: can the
/// store ever end up saying "yes" when the host did not?
/// </summary>
public sealed class AutoAcceptStoreTests : IDisposable
{
    private static readonly Guid GuestUser = new("aaaaaaaa-0000-4000-8000-000000000001");
    private static readonly Guid GuestDevice = new("bbbbbbbb-0000-4000-8000-000000000002");
    private static readonly DateTimeOffset Granted = new(2026, 9, 18, 12, 0, 0, TimeSpan.Zero);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "josour-autoaccept-" + Guid.NewGuid().ToString("N"));

    private string FilePath => Path.Combine(_dir, "auto-accept.json");

    private static TrustedGuest Guest(Guid? userId = null, Guid? deviceId = null) => new(
        userId ?? GuestUser,
        deviceId ?? GuestDevice,
        "Sara Ahmed",
        "SARA-LAPTOP",
        MaxDurationMinutes: 45,
        ExpiresAt: Granted.AddDays(7),
        AllowlistVersion: 3,
        GrantedAt: Granted);

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    [Fact]
    public void A_missing_file_means_off()
    {
        var store = new AutoAcceptStore(FilePath);

        Assert.False(store.Current.Enabled);
        Assert.Empty(store.Current.Guests);
        Assert.False(File.Exists(FilePath));
    }

    [Fact]
    public async Task A_saved_rule_comes_back_whole()
    {
        var store = new AutoAcceptStore(FilePath);
        await store.SaveAsync(new AutoAcceptSettings { Enabled = true, Guests = new[] { Guest() } }, CancellationToken.None);

        var reloaded = new AutoAcceptStore(FilePath).Current;

        Assert.True(reloaded.Enabled);
        var guest = Assert.Single(reloaded.Guests);
        Assert.Equal(GuestUser, guest.GuestUserId);
        Assert.Equal(GuestDevice, guest.GuestDeviceId);
        Assert.Equal(45, guest.MaxDurationMinutes);
        Assert.Equal(Granted.AddDays(7), guest.ExpiresAt);
        Assert.Equal(3, guest.AllowlistVersion);
    }

    [Fact]
    public async Task Saving_raises_Changed_and_updates_Current_without_a_reload()
    {
        var store = new AutoAcceptStore(FilePath);
        AutoAcceptSettings? seen = null;
        store.Changed += (_, s) => seen = s;

        await store.SaveAsync(new AutoAcceptSettings { Enabled = true }, CancellationToken.None);

        Assert.True(store.Current.Enabled);
        Assert.NotNull(seen);
        Assert.True(seen!.Enabled);
    }

    [Fact]
    public async Task A_corrupt_file_reads_as_off_rather_than_as_anything_else()
    {
        Directory.CreateDirectory(_dir);
        await File.WriteAllTextAsync(FilePath, "{ this is not json");

        var store = new AutoAcceptStore(FilePath);

        Assert.False(store.Current.Enabled);
        Assert.Empty(store.Current.Guests);
    }

    [Fact]
    public async Task An_entry_with_no_identity_is_dropped_on_the_way_in()
    {
        // Such a row can only match Guid.Empty, which the policy refuses anyway; leaving it in the file would put a
        // decision in the settings list that does not correspond to any guest.
        Directory.CreateDirectory(_dir);
        await File.WriteAllTextAsync(
            FilePath,
            """
            {
              "enabled": true,
              "guests": [
                {
                  "guestUserId": "00000000-0000-0000-0000-000000000000",
                  "guestDeviceId": "00000000-0000-0000-0000-000000000000",
                  "guestName": "Nobody",
                  "guestDevice": "NOWHERE",
                  "maxDurationMinutes": 60,
                  "expiresAt": null,
                  "allowlistVersion": 1,
                  "grantedAt": "2026-09-18T12:00:00+00:00"
                }
              ]
            }
            """);

        var store = new AutoAcceptStore(FilePath);

        Assert.True(store.Current.Enabled);
        Assert.Empty(store.Current.Guests);
    }

    [Fact]
    public async Task An_empty_file_reads_as_off()
    {
        Directory.CreateDirectory(_dir);
        await File.WriteAllTextAsync(FilePath, "null");

        Assert.False(new AutoAcceptStore(FilePath).Current.Enabled);
    }

    [Fact]
    public async Task Turning_the_switch_off_leaves_the_rules_in_place()
    {
        var store = new AutoAcceptStore(FilePath);
        await store.SaveAsync(new AutoAcceptSettings { Enabled = true, Guests = new[] { Guest() } }, CancellationToken.None);

        await store.SaveAsync(store.Current with { Enabled = false }, CancellationToken.None);

        var reloaded = new AutoAcceptStore(FilePath).Current;
        Assert.False(reloaded.Enabled);
        Assert.Single(reloaded.Guests);
    }

    [Theory]
    [InlineData("""{ "enabled": true, "guests": null }""")]
    [InlineData("""{ "enabled": true, "guests": [null] }""")]
    [InlineData("""{ "enabled": true }""")]
    public async Task A_hand_edited_file_with_no_usable_guest_list_still_loads(string json)
    {
        // The file is meant to be readable, so somebody will edit it. None of these may throw: the exception would
        // escape the Lazy behind Current and every later read would rethrow it, taking the app down long after.
        Directory.CreateDirectory(_dir);
        await File.WriteAllTextAsync(FilePath, json);

        var store = new AutoAcceptStore(FilePath);

        Assert.True(store.Current.Enabled);
        Assert.Empty(store.Current.Guests);
        Assert.Null(store.Current.Find(GuestUser, GuestDevice));
    }

    [Fact]
    public async Task No_temporary_file_is_left_behind()
    {
        var store = new AutoAcceptStore(FilePath);
        await store.SaveAsync(new AutoAcceptSettings { Enabled = true }, CancellationToken.None);

        Assert.False(File.Exists(FilePath + ".tmp"));
    }
}
