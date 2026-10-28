using System.Globalization;
using Josour.App.Services;

namespace Josour.App.Tests;

/// <summary>The two window-less runs that put the system proxy back, and the launch agent that runs one of them at login.</summary>
public sealed class SystemProxyStartupTests
{
    [Fact]
    public void The_guardian_switch_carries_the_pid_and_the_start_time()
    {
        var started = new DateTimeOffset(2026, 10, 21, 9, 30, 0, TimeSpan.Zero);
        var options = StartupOptions.Parse(new[]
        {
            StartupOptions.ProxyGuardianSwitch, "4242", started.UtcTicks.ToString(CultureInfo.InvariantCulture),
        });

        Assert.Equal(new ProxyGuardianTarget(4242, started), options.ProxyGuardian);
        Assert.False(options.RestoreProxy);
        Assert.False(options.StartMinimized);
    }

    [Theory]
    [InlineData("x", "1")]
    [InlineData("12", "-5")]
    [InlineData("12", "not-a-number")]
    public void A_malformed_guardian_switch_starts_no_guardian(string pid, string ticks)
    {
        var options = StartupOptions.Parse(new[] { StartupOptions.ProxyGuardianSwitch, pid, ticks, StartupOptions.MinimizedSwitch });
        Assert.Null(options.ProxyGuardian);
        Assert.True(options.StartMinimized); // and the arguments it would have taken are not swallowed into something else
    }

    [Fact]
    public void The_restore_switch_is_read()
    {
        Assert.True(StartupOptions.Parse(new[] { StartupOptions.RestoreProxySwitch }).RestoreProxy);
        Assert.False(StartupOptions.Parse(Array.Empty<string>()).RestoreProxy);
    }

    [Fact]
    public void The_login_restore_agent_runs_the_executable_with_the_restore_switch()
    {
        var plist = SystemProxySafetyNet.BuildLaunchAgent("/Applications/Josour & Co.app/Contents/MacOS/Josour");

        Assert.Contains("<string>com.josour.proxy-restore</string>", plist);
        Assert.Contains("<string>/Applications/Josour &amp; Co.app/Contents/MacOS/Josour</string>", plist);
        Assert.Contains("<string>--restore-proxy</string>", plist);
        Assert.Contains("<key>RunAtLoad</key>", plist);
    }

    [Theory]
    [InlineData("/usr/local/share/dotnet/dotnet")]
    [InlineData(@"C:\Program Files\dotnet\dotnet.exe")]
    public void The_dotnet_host_is_never_registered_as_the_restore_at_login(string executable)
    {
        Assert.True(SystemProxySafetyNet.IsDotnetHost(executable));
        Assert.NotNull(SystemProxySafetyNet.TransientReason(executable));
    }

    [Fact]
    public void An_installed_copy_is_registered_and_a_copy_run_from_the_disk_image_is_not()
    {
        Assert.Null(SystemProxySafetyNet.TransientReason("/Applications/Josour.app/Contents/MacOS/Josour"));
        Assert.Null(SystemProxySafetyNet.TransientReason(@"C:\Users\sara\AppData\Local\Programs\Josour\Josour.exe"));
        if (OperatingSystem.IsMacOS())
        {
            Assert.NotNull(SystemProxySafetyNet.TransientReason("/Volumes/Josour/Josour.app/Contents/MacOS/Josour"));
        }
    }
}
