using System.Runtime.Versioning;
using Josour.Infrastructure.Security;

namespace Josour.Infrastructure.Tests;

/// <summary>
/// The macOS half of secret storage. These talk to the real Keychain, so they run only on macOS and only under a
/// service name of their own — a test must never be able to delete the items a real sign-in put there.
/// </summary>
[SupportedOSPlatform("macos")]
public sealed class KeychainSecretStoreTests : IDisposable
{
    private const string Key = "refresh_token";

    private readonly string _service = "com.josour.tests." + Guid.NewGuid().ToString("N");
    private readonly List<string> _written = new();

    private KeychainSecretStore Store()
    {
        var store = new KeychainSecretStore(_service);
        return store;
    }

    public void Dispose()
    {
        foreach (var key in _written)
        {
            try
            {
                new KeychainSecretStore(_service).RemoveAsync(key, CancellationToken.None).GetAwaiter().GetResult();
            }
            catch (MacKeychainException)
            {
                // Best effort: a leftover item under a random test service name harms nothing.
            }
        }
    }

    private async Task WriteAsync(KeychainSecretStore store, string key, string value)
    {
        _written.Add(key);
        await store.SetAsync(key, value, CancellationToken.None);
    }

    [MacFact]
    public async Task A_stored_secret_comes_back()
    {
        var store = Store();
        await WriteAsync(store, Key, "s3cret-value");

        Assert.Equal("s3cret-value", await store.GetAsync(Key, CancellationToken.None));
    }

    [MacFact]
    public async Task A_key_that_was_never_written_is_null_rather_than_an_error()
    {
        Assert.Null(await Store().GetAsync("never_stored", CancellationToken.None));
    }

    [MacFact]
    public async Task Writing_twice_replaces_the_value_instead_of_adding_a_second_item()
    {
        // SecItemAdd answers errSecDuplicateItem the second time; getting this wrong leaves the old token in place
        // and the client then reconnects with a credential the server has already rotated away.
        var store = Store();
        await WriteAsync(store, Key, "first");
        await WriteAsync(store, Key, "second");

        Assert.Equal("second", await store.GetAsync(Key, CancellationToken.None));
    }

    [MacFact]
    public async Task Removing_makes_it_absent_again()
    {
        var store = Store();
        await WriteAsync(store, Key, "value");

        await store.RemoveAsync(Key, CancellationToken.None);

        Assert.Null(await store.GetAsync(Key, CancellationToken.None));
    }

    [MacFact]
    public async Task Removing_something_that_is_not_there_is_not_an_error()
    {
        await Store().RemoveAsync("never_stored", CancellationToken.None);
    }

    [MacFact]
    public async Task Two_keys_under_one_service_do_not_collide()
    {
        var store = Store();
        await WriteAsync(store, "refresh_token", "token");
        await WriteAsync(store, "device_secret", "secret");

        Assert.Equal("token", await store.GetAsync("refresh_token", CancellationToken.None));
        Assert.Equal("secret", await store.GetAsync("device_secret", CancellationToken.None));
    }

    [MacFact]
    public async Task A_non_ascii_value_survives_the_round_trip()
    {
        // The value goes through CFData as UTF-8; a length taken in characters rather than bytes would truncate here.
        var store = Store();
        await WriteAsync(store, Key, "قيمة سرية ☕");

        Assert.Equal("قيمة سرية ☕", await store.GetAsync(Key, CancellationToken.None));
    }

    [MacTheory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("Upper")]
    [InlineData("has space")]
    [InlineData("../escape")]
    public async Task A_key_the_Windows_store_would_refuse_is_refused_here_too(string key)
    {
        await Assert.ThrowsAnyAsync<ArgumentException>(
            () => Store().GetAsync(key, CancellationToken.None));
    }
}
