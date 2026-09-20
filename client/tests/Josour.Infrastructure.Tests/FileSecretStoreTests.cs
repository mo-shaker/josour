using Josour.Infrastructure.Security;

namespace Josour.Infrastructure.Tests;

/// <summary>
/// The store macOS uses, after the Keychain turned out to be unusable from an unsigned app.
/// <para>
/// Its whole security claim is the file mode, so that is what most of these assert. The claim is narrow on
/// purpose: other users on the machine are kept out, another process running as the same user is not — and
/// neither does DPAPI keep that process out on Windows, so the two platforms are level where it matters.
/// </para>
/// </summary>
public sealed class FileSecretStoreTests : IDisposable
{
    private const string Key = "refresh_token";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "josour-secrets-" + Guid.NewGuid().ToString("N"));

    private FileSecretStore Store() => new(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    [Fact]
    public async Task A_stored_secret_comes_back()
    {
        var store = Store();
        await store.SetAsync(Key, "s3cret-value", CancellationToken.None);

        Assert.Equal("s3cret-value", await store.GetAsync(Key, CancellationToken.None));
    }

    [Fact]
    public async Task A_key_that_was_never_written_is_null_rather_than_an_error()
    {
        Assert.Null(await Store().GetAsync("never_stored", CancellationToken.None));
    }

    [Fact]
    public async Task Writing_twice_replaces_the_value()
    {
        var store = Store();
        await store.SetAsync(Key, "first", CancellationToken.None);
        await store.SetAsync(Key, "second", CancellationToken.None);

        Assert.Equal("second", await store.GetAsync(Key, CancellationToken.None));
    }

    [Fact]
    public async Task Removing_makes_it_absent_again()
    {
        var store = Store();
        await store.SetAsync(Key, "value", CancellationToken.None);

        await store.RemoveAsync(Key, CancellationToken.None);

        Assert.Null(await store.GetAsync(Key, CancellationToken.None));
    }

    [Fact]
    public async Task Removing_something_that_is_not_there_is_not_an_error()
    {
        await Store().RemoveAsync("never_stored", CancellationToken.None);
    }

    [Fact]
    public async Task Two_keys_do_not_collide()
    {
        var store = Store();
        await store.SetAsync("refresh_token", "token", CancellationToken.None);
        await store.SetAsync("device_secret", "secret", CancellationToken.None);

        Assert.Equal("token", await store.GetAsync("refresh_token", CancellationToken.None));
        Assert.Equal("secret", await store.GetAsync("device_secret", CancellationToken.None));
    }

    [Fact]
    public async Task A_non_ascii_value_survives_the_round_trip()
    {
        var store = Store();
        await store.SetAsync(Key, "قيمة سرية ☕", CancellationToken.None);

        Assert.Equal("قيمة سرية ☕", await store.GetAsync(Key, CancellationToken.None));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("Upper")]
    [InlineData("has space")]
    [InlineData("../escape")]
    public async Task A_key_the_windows_store_would_refuse_is_refused_here_too(string key)
    {
        await Assert.ThrowsAnyAsync<ArgumentException>(() => Store().GetAsync(key, CancellationToken.None));
    }

    [Fact]
    public async Task No_temporary_file_is_left_behind()
    {
        var store = Store();
        await store.SetAsync(Key, "value", CancellationToken.None);

        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
    }

    // ---------------------------------------------------------------- the permissions, which are the point

    [UnixFact]
    public async Task The_secret_is_readable_by_its_owner_and_nobody_else()
    {
        var store = Store();
        await store.SetAsync(Key, "value", CancellationToken.None);

        var mode = File.GetUnixFileMode(Directory.GetFiles(_dir, "*.secret").Single());

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, mode);
    }

    [UnixFact]
    public async Task The_directory_holding_them_is_owner_only_too()
    {
        // A file nobody else may read is not much use inside a directory anyone may list.
        await Store().SetAsync(Key, "value", CancellationToken.None);

        var mode = File.GetUnixFileMode(_dir);

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, mode);
    }

    [UnixFact]
    public async Task A_file_that_became_readable_by_others_is_tightened_rather_than_refused()
    {
        // Restored from a backup, copied with the wrong tool. Refusing would lock the user out of their account
        // to protect them from a permission bit; the value is about to be re-read either way.
        var store = Store();
        await store.SetAsync(Key, "value", CancellationToken.None);
        var path = Directory.GetFiles(_dir, "*.secret").Single();
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.OtherRead);

        var value = await store.GetAsync(Key, CancellationToken.None);

        Assert.Equal("value", value);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
    }

    [UnixFact]
    public async Task The_value_is_never_world_readable_even_for_an_instant()
    {
        // The mode goes on the temp file before the bytes do. Writing first and narrowing afterwards would leave
        // a window, however short, where the token is readable by anyone on the machine — and a window that
        // small is exactly the kind nobody notices is there.
        var store = Store();
        await store.SetAsync(Key, "first", CancellationToken.None);

        // Overwrite repeatedly while watching for any file in the directory that is not owner-only.
        for (var i = 0; i < 20; i++)
        {
            await store.SetAsync(Key, $"value-{i}", CancellationToken.None);
            foreach (var file in Directory.GetFiles(_dir))
            {
                var mode = File.GetUnixFileMode(file);
                Assert.False(
                    mode.HasFlag(UnixFileMode.GroupRead) || mode.HasFlag(UnixFileMode.OtherRead),
                    $"{Path.GetFileName(file)} was readable beyond its owner ({mode}).");
            }
        }
    }
}
