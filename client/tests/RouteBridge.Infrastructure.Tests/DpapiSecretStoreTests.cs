using RouteBridge.Infrastructure.Security;

namespace RouteBridge.Infrastructure.Tests;

public sealed class DpapiSecretStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rb-secrets-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    [Fact]
    public async Task RoundTrip_ReturnsStoredValue()
    {
        var store = new DpapiSecretStore(_dir);

        await store.SetAsync("refresh_token", "value-1", CancellationToken.None);
        var read = await store.GetAsync("refresh_token", CancellationToken.None);

        Assert.Equal("value-1", read);
        Assert.True(File.Exists(Path.Combine(_dir, "refresh_token.bin")));
    }

    [Fact]
    public async Task Set_OverwritesPreviousValue()
    {
        var store = new DpapiSecretStore(_dir);

        await store.SetAsync("device.secret", "first", CancellationToken.None);
        await store.SetAsync("device.secret", "second", CancellationToken.None);

        Assert.Equal("second", await store.GetAsync("device.secret", CancellationToken.None));
    }

    [Fact]
    public async Task Get_MissingKey_ReturnsNull()
    {
        var store = new DpapiSecretStore(_dir);

        Assert.Null(await store.GetAsync("does-not-exist", CancellationToken.None));
    }

    [Fact]
    public async Task Remove_DeletesFile_AndIsIdempotent()
    {
        var store = new DpapiSecretStore(_dir);
        await store.SetAsync("k1", "v", CancellationToken.None);

        await store.RemoveAsync("k1", CancellationToken.None);
        await store.RemoveAsync("k1", CancellationToken.None);

        Assert.Null(await store.GetAsync("k1", CancellationToken.None));
        Assert.False(File.Exists(Path.Combine(_dir, "k1.bin")));
    }

    [Fact]
    public async Task StoredFile_IsNeverBareValue_AndCarriesFormatTag()
    {
        var store = new DpapiSecretStore(_dir);
        await store.SetAsync("tagged", "hello", CancellationToken.None);

        var bytes = await File.ReadAllBytesAsync(Path.Combine(_dir, "tagged.bin"));
        var tag = System.Text.Encoding.ASCII.GetString(bytes, 0, 8);

        Assert.Equal(OperatingSystem.IsWindows() ? "RBDPAPI1" : "RBPLAIN1", tag);
        Assert.Equal(OperatingSystem.IsWindows(), DpapiSecretStore.IsProtectionAvailable);
    }

    [Fact]
    public async Task Get_UnknownFormat_ReturnsNull()
    {
        Directory.CreateDirectory(_dir);
        await File.WriteAllBytesAsync(Path.Combine(_dir, "garbage.bin"), "XXXXXXXXsomething"u8.ToArray());
        var store = new DpapiSecretStore(_dir);

        Assert.Null(await store.GetAsync("garbage", CancellationToken.None));
    }

    [Theory]
    [InlineData("")]
    [InlineData("Refresh")]
    [InlineData("a b")]
    [InlineData("../x")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("tok:en")]
    public async Task InvalidKey_Throws(string key)
    {
        var store = new DpapiSecretStore(_dir);

        await Assert.ThrowsAsync<ArgumentException>(() => store.GetAsync(key, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => store.SetAsync(key, "v", CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => store.RemoveAsync(key, CancellationToken.None));
    }

    [Theory]
    [InlineData("refresh_token")]
    [InlineData("device.secret")]
    [InlineData("a-b_c.d9")]
    public void ValidKey_Accepted(string key)
    {
        DpapiSecretStore.ValidateKey(key);
    }

    [Fact]
    public async Task Set_NullValue_Throws()
    {
        var store = new DpapiSecretStore(_dir);

        await Assert.ThrowsAsync<ArgumentNullException>(() => store.SetAsync("k", null!, CancellationToken.None));
    }
}
