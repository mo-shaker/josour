using Josour.Browser.Registry;
using Josour.Core.Browser;

namespace Josour.Browser.Tests;

public class BrowserLauncherTests
{
    [Fact]
    public async Task NonWindows_LaunchFailsWithClearMessage()
    {
        if (OperatingSystem.IsWindows()) return; // on Windows this launches a real browser; it is covered by the Spike browser tool
        await using var launcher = new BrowserLauncher(NullRegistryReader.Instance);
        Assert.False(launcher.IsRunning);
        Assert.False(launcher.OwnsProcess(Environment.ProcessId));

        var result = await launcher.LaunchAsync(new BrowserLaunchOptions(BrowserKind.Chrome, 8080, Path.GetTempPath(), "http://check.josour/"), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(BrowserLaunchFailure.Other, result.Failure);
        Assert.Contains("Windows-only", result.Detail);
        Assert.False(launcher.IsRunning);
        Assert.False(launcher.InstanceHandoff);
        await launcher.CloseAsync(TimeSpan.FromSeconds(1), CancellationToken.None); // nothing to close, and it does not throw
    }

    [Fact]
    public void ImplementsCoreContract()
    {
        Assert.IsAssignableFrom<IBrowserSession>(new BrowserLauncher(NullRegistryReader.Instance));
    }
}
