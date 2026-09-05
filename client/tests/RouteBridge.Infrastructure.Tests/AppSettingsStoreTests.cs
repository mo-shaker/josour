using RouteBridge.Infrastructure.Settings;

namespace RouteBridge.Infrastructure.Tests;

public sealed class AppSettingsStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rb-settings-" + Guid.NewGuid().ToString("N"));

    private string FilePath => Path.Combine(_dir, "settings.json");

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    [Fact]
    public void Defaults_WhenFileMissing()
    {
        var store = new AppSettingsStore(FilePath);

        Assert.Equal(string.Empty, store.Current.ServerUrl);
        Assert.Equal(BrowserPreference.Auto, store.Current.PreferredBrowser);
        Assert.False(store.Current.StartMinimized);
        Assert.Equal(string.Empty, store.Current.Language); // empty means "the default language", i.e. Arabic
        Assert.False(File.Exists(FilePath));
    }

    [Fact]
    public async Task Language_RoundTripsAsACode_AndAnOldFileWithoutItStillLoads()
    {
        var store = new AppSettingsStore(FilePath);
        await store.SaveAsync(new AppSettings { ServerUrl = "https://routebridge.example.com", Language = "en" }, CancellationToken.None);

        Assert.Contains("\"language\": \"en\"", await File.ReadAllTextAsync(FilePath));
        Assert.Equal("en", new AppSettingsStore(FilePath).Current.Language);

        // A settings.json written before week 5 has no "language" at all: it must load and mean "Arabic".
        await File.WriteAllTextAsync(FilePath, """{"serverUrl":"https://routebridge.example.com","startMinimized":true}""");
        var upgraded = new AppSettingsStore(FilePath);

        Assert.Equal(string.Empty, upgraded.Current.Language);
        Assert.True(upgraded.Current.StartMinimized);
    }

    [Fact]
    public async Task RoundTrip_ThroughDisk_AndChangedEvent()
    {
        var store = new AppSettingsStore(FilePath);
        AppSettings? announced = null;
        store.Changed += (_, s) => announced = s;
        var settings = new AppSettings { ServerUrl = "https://routebridge.example.com", PreferredBrowser = BrowserPreference.Edge, StartMinimized = true };

        await store.SaveAsync(settings, CancellationToken.None);

        Assert.Equal(settings, store.Current);
        Assert.Equal(settings, announced);
        Assert.True(File.Exists(FilePath));
        Assert.False(File.Exists(FilePath + ".tmp"));

        var text = await File.ReadAllTextAsync(FilePath);
        Assert.Contains("\"serverUrl\": \"https://routebridge.example.com\"", text);
        Assert.Contains("\"preferredBrowser\": \"edge\"", text);
        Assert.Contains("\"startMinimized\": true", text);

        var reloaded = new AppSettingsStore(FilePath);
        Assert.Equal(settings, reloaded.Current);
    }

    [Fact]
    public async Task HandEditedFile_IsReadCaseInsensitively_WithTrimmedUrl()
    {
        Directory.CreateDirectory(_dir);
        await File.WriteAllTextAsync(FilePath, """
            {
              // hand edited
              "ServerUrl": "  https://x.example  ",
              "PreferredBrowser": "CHROME",
              "startMinimized": false,
            }
            """);

        var store = new AppSettingsStore(FilePath);

        Assert.Equal("https://x.example", store.Current.ServerUrl);
        Assert.Equal(BrowserPreference.Chrome, store.Current.PreferredBrowser);
    }

    [Fact]
    public async Task CorruptFile_FallsBackToDefaults_AndSaveOverwritesIt()
    {
        Directory.CreateDirectory(_dir);
        await File.WriteAllTextAsync(FilePath, "{ this is not json");

        var store = new AppSettingsStore(FilePath);
        Assert.Equal(AppSettings.Default, store.Current);

        await store.SaveAsync(new AppSettings { ServerUrl = "https://ok.example" }, CancellationToken.None);

        Assert.Equal("https://ok.example", new AppSettingsStore(FilePath).Current.ServerUrl);
    }

    [Fact]
    public async Task UnknownBrowserValue_FallsBackToDefaults()
    {
        Directory.CreateDirectory(_dir);
        await File.WriteAllTextAsync(FilePath, """{"serverUrl":"https://x.example","preferredBrowser":"firefox"}""");

        var store = new AppSettingsStore(FilePath);

        Assert.Equal(AppSettings.Default, store.Current);
    }
}
