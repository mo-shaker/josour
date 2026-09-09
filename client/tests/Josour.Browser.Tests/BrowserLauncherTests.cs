using Josour.Browser.Registry;
using Josour.Core.Browser;

namespace Josour.Browser.Tests;

public class BrowserLauncherTests
{
    [Fact]
    public async Task NonWindows_LaunchFailsWithClearMessage()
    {
        if (OperatingSystem.IsWindows()) return; // على Windows هذا يشغّل متصفحًا فعليًا؛ يُغطى بأداة Spike browser
        await using var launcher = new BrowserLauncher(NullRegistryReader.Instance);
        Assert.False(launcher.IsRunning);
        Assert.False(launcher.OwnsProcess(Environment.ProcessId));

        var result = await launcher.LaunchAsync(new BrowserLaunchOptions(BrowserKind.Chrome, 8080, Path.GetTempPath(), "http://check.josour/"), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(BrowserLaunchFailure.Other, result.Failure);
        Assert.Contains("Windows-only", result.Detail);
        Assert.False(launcher.IsRunning);
        Assert.False(launcher.InstanceHandoff);
        await launcher.CloseAsync(TimeSpan.FromSeconds(1), CancellationToken.None); // لا شيء يُغلق ولا يرمي
    }

    [Fact]
    public void ImplementsCoreContract()
    {
        Assert.IsAssignableFrom<IBrowserSession>(new BrowserLauncher(NullRegistryReader.Instance));
    }
}
