using RouteBridge.Infrastructure.Diagnostics;
using RouteBridge.Infrastructure.Settings;
using RouteBridge.Infrastructure.Tests.Support;

namespace RouteBridge.Infrastructure.Tests;

/// <summary>
/// The About / diagnostics surface. It exists so a support conversation has something to quote, and it has exactly one
/// rule that can be broken silently: no secret may appear on it.
/// </summary>
public sealed class SupportInfoTests
{
    private static readonly AppSettings Settings = new() { ServerUrl = "https://routebridge.example.com" };

    [Fact]
    public void ItCarriesWhatSupportAsksFor()
    {
        var auth = FakeAuthSession.SignedInAs();

        var info = SupportInfo.Build(new FakeDeviceInfoProvider(), auth, Settings, "connected", @"C:\logs");

        Assert.Equal("0.2.0-test", info.AppVersion);
        Assert.Equal("TEST-PC", info.DeviceName);
        Assert.Equal(auth.DeviceId!.Value.ToString(), info.DeviceId);
        Assert.Equal("https://routebridge.example.com", info.ServerUrl);
        Assert.Equal("guest@example.com", info.SignedInAs);
        Assert.Equal("connected", info.ConnectionState);
        Assert.Equal(@"C:\logs", info.LogDirectory);
    }

    [Fact]
    public void NoTokenAndNoDeviceSecret_Appears()
    {
        const string token = "at-secret-value-0123456789";
        var auth = FakeAuthSession.SignedInAs(accessToken: token);

        var text = SupportInfo.Build(new FakeDeviceInfoProvider(), auth, Settings, "connected").ToText();

        Assert.DoesNotContain(token, text, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("token", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SignedOut_ShowsEmptyIdentityFields_RatherThanStaleOnes()
    {
        var info = SupportInfo.Build(new FakeDeviceInfoProvider(), auth: null, settings: null, "offline");

        Assert.Equal(string.Empty, info.DeviceId);
        Assert.Equal(string.Empty, info.SignedInAs);
        Assert.Equal(string.Empty, info.ServerUrl);
        Assert.Equal("offline", info.ConnectionState);
        Assert.Equal(RouteBridge.Infrastructure.Logging.LoggingSetup.DefaultLogDirectory, info.LogDirectory);
    }

    [Fact]
    public void ThePairsAreStableAndComplete()
    {
        var pairs = SupportInfo.Build(new FakeDeviceInfoProvider(), FakeAuthSession.SignedInAs(), Settings, "connected").ToPairs();

        Assert.Equal(
            new[] { "app_version", "device_name", "device_id", "os_version", "os_build", "server_url", "signed_in_as", "connection", "logs" },
            pairs.Select(p => p.Key).ToArray());
    }
}
