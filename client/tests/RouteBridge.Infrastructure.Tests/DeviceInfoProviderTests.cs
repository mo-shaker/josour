using System.Reflection;
using RouteBridge.Infrastructure.Device;

namespace RouteBridge.Infrastructure.Tests;

public sealed class DeviceInfoProviderTests
{
    [Fact]
    public void AllFields_AreNonEmpty()
    {
        var provider = new DeviceInfoProvider();

        Assert.False(string.IsNullOrWhiteSpace(provider.DeviceName));
        Assert.False(string.IsNullOrWhiteSpace(provider.OsVersion));
        Assert.False(string.IsNullOrWhiteSpace(provider.OsBuild));
        Assert.False(string.IsNullOrWhiteSpace(provider.AppVersion));
    }

    [Fact]
    public void Snapshot_MatchesProperties_AndIsStable()
    {
        var provider = new DeviceInfoProvider();

        var first = provider.GetDeviceInfo();
        var second = provider.GetDeviceInfo();

        Assert.Same(first, second);
        Assert.Equal(provider.DeviceName, first.DeviceName);
        Assert.Equal(provider.OsVersion, first.OsVersion);
        Assert.Equal(provider.OsBuild, first.OsBuild);
        Assert.Equal(provider.AppVersion, first.AppVersion);
    }

    [Fact]
    public void DeviceName_IsMachineName()
    {
        Assert.Equal(Environment.MachineName, new DeviceInfoProvider().DeviceName);
    }

    [Fact]
    public void AppVersion_ComesFromInformationalVersion_WithoutCommitSuffix()
    {
        var assembly = typeof(DeviceInfoProvider).Assembly;
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        Assert.False(string.IsNullOrWhiteSpace(informational));

        var provider = new DeviceInfoProvider(assembly);

        var expected = informational!.Split('+')[0];
        Assert.Equal(expected, provider.AppVersion);
        Assert.DoesNotContain('+', provider.AppVersion);
    }

    [Fact]
    public void OsVersion_OnWindows_MentionsWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var provider = new DeviceInfoProvider();

        Assert.Contains("Windows", provider.OsVersion, StringComparison.OrdinalIgnoreCase);
        Assert.Matches(@"^\d+(\.\d+)?$", provider.OsBuild);
    }
}
