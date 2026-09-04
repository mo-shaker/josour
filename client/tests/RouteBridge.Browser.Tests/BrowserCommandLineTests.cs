using RouteBridge.Core.Browser;

namespace RouteBridge.Browser.Tests;

public class BrowserCommandLineTests
{
    [Theory]
    [InlineData(BrowserKind.Chrome)]
    [InlineData(BrowserKind.Edge)]
    public void Arguments_MatchPlanSection82_Exactly(BrowserKind kind)
    {
        var options = new BrowserLaunchOptions(kind, 54321, @"C:\Users\u\AppData\Local\RouteBridge\BrowserProfile", "http://check.routebridge/");
        var args = BrowserCommandLine.Arguments(options);
        Assert.Equal(new[]
        {
            @"--user-data-dir=C:\Users\u\AppData\Local\RouteBridge\BrowserProfile",
            "--proxy-server=http://127.0.0.1:54321",
            "--no-first-run",
            "--no-default-browser-check",
            "--disable-sync",
            "--disable-background-networking",
            "--disable-component-update",
            "--disable-quic",
            "--force-webrtc-ip-handling-policy=disable_non_proxied_udp",
            "--hide-crash-restore-bubble",
            "--new-window",
            "http://check.routebridge/",
        }, args);
        Assert.DoesNotContain(args, a => a.Contains("proxy-bypass-list", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Render_QuotesArgumentsWithSpaces()
    {
        var options = new BrowserLaunchOptions(BrowserKind.Chrome, 8080, @"C:\Program Files\x\Profile Dir", "http://check.routebridge/");
        var rendered = BrowserCommandLine.Render(@"C:\Program Files\Google\Chrome\Application\chrome.exe", BrowserCommandLine.Arguments(options));
        Assert.StartsWith("\"C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe\" --user-data-dir=\"C:\\Program Files\\x\\Profile Dir\" --proxy-server=http://127.0.0.1:8080 --no-first-run", rendered);
        Assert.EndsWith("--new-window http://check.routebridge/", rendered);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(70000)]
    public void Arguments_RejectBadPort(int port)
    {
        var options = new BrowserLaunchOptions(BrowserKind.Chrome, port, "p", "http://check.routebridge/");
        Assert.Throws<ArgumentOutOfRangeException>(() => BrowserCommandLine.Arguments(options));
    }

    [Fact]
    public void DefaultProfileDirectory_IsUnderLocalAppData()
    {
        var dir = BrowserCommandLine.DefaultProfileDirectory();
        Assert.EndsWith(Path.Combine("RouteBridge", "BrowserProfile"), dir);
        Assert.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), dir);
    }
}
