using RouteBridge.Browser.Registry;
using RouteBridge.Core.Browser;

namespace RouteBridge.Browser.Tests;

public class BrowserLocatorTests
{
    private const string AppPaths = @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\chrome.exe";
    private const string AppPathsWow = @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\App Paths\chrome.exe";

    private static readonly Func<string, string?> Env = name => name switch
    {
        "ProgramFiles" => @"C:\Program Files",
        "ProgramFiles(x86)" => @"C:\Program Files (x86)",
        "LocalAppData" => @"C:\Users\u\AppData\Local",
        _ => null,
    };

    private static BrowserLocator Create(FakeRegistry registry, params string[] existing)
    {
        var set = new HashSet<string>(existing, StringComparer.OrdinalIgnoreCase);
        return new BrowserLocator(registry, set.Contains, _ => (true, "Google LLC"), Env);
    }

    [Fact]
    public void Prefers_HklmAppPaths()
    {
        var registry = new FakeRegistry()
            .Set(RegistryRoot.LocalMachine, AppPaths, null, @"C:\hklm\chrome.exe")
            .Set(RegistryRoot.CurrentUser, AppPaths, null, @"C:\hkcu\chrome.exe");
        var location = Create(registry, @"C:\hklm\chrome.exe", @"C:\hkcu\chrome.exe").Locate(BrowserKind.Chrome);
        Assert.NotNull(location);
        Assert.Equal(@"C:\hklm\chrome.exe", location!.Path);
        Assert.Equal("app_paths:HKLM", location.Source);
        Assert.True(location.PublisherVerified);
        Assert.Equal("Google LLC", location.Publisher);
    }

    [Fact]
    public void FallsBack_ToHkcu_ThenWow6432Node_WhenFilesMissing()
    {
        var registry = new FakeRegistry()
            .Set(RegistryRoot.LocalMachine, AppPaths, null, @"C:\missing\chrome.exe")
            .Set(RegistryRoot.CurrentUser, AppPaths, null, "\"C:\\hkcu\\chrome.exe\"")
            .Set(RegistryRoot.LocalMachine, AppPathsWow, null, @"C:\wow\chrome.exe");
        Assert.Equal("app_paths:HKCU", Create(registry, @"C:\hkcu\chrome.exe", @"C:\wow\chrome.exe").Locate(BrowserKind.Chrome)!.Source);
        Assert.Equal(@"app_paths:HKLM\WOW6432Node", Create(registry, @"C:\wow\chrome.exe").Locate(BrowserKind.Chrome)!.Source);
    }

    [Fact]
    public void FallsBack_ToDefaultPaths_InOrder()
    {
        var registry = new FakeRegistry();
        var local = @"C:\Users\u\AppData\Local\Google\Chrome\Application\chrome.exe";
        var x86 = @"C:\Program Files (x86)\Google\Chrome\Application\chrome.exe";
        var pf = @"C:\Program Files\Google\Chrome\Application\chrome.exe";
        Assert.Equal(local, Create(registry, local).Locate(BrowserKind.Chrome)!.Path);
        Assert.Equal(x86, Create(registry, local, x86).Locate(BrowserKind.Chrome)!.Path);
        var all = Create(registry, local, x86, pf).Locate(BrowserKind.Chrome)!;
        Assert.Equal(pf, all.Path);
        Assert.Equal("default", all.Source);
    }

    [Fact]
    public void Edge_DefaultPaths_PreferX86()
    {
        var paths = BrowserLocator.DefaultPaths(BrowserKind.Edge, Env);
        Assert.Equal(new[]
        {
            @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe",
            @"C:\Program Files\Microsoft\Edge\Application\msedge.exe",
        }, paths);
    }

    [Fact]
    public void NotFound_ReturnsNull()
    {
        Assert.Null(Create(new FakeRegistry()).Locate(BrowserKind.Chrome));
        Assert.Null(Create(new FakeRegistry()).Locate(BrowserKind.Edge));
    }

    [Theory]
    [InlineData("Google LLC", true, true)]
    [InlineData("google llc", true, true)]
    [InlineData("Evil Corp", true, false)]
    [InlineData(null, false, false)]
    public void PublisherVerified_RequiresExpectedOrganization(string? publisher, bool signed, bool expected)
    {
        var registry = new FakeRegistry().Set(RegistryRoot.LocalMachine, AppPaths, null, @"C:\c\chrome.exe");
        var locator = new BrowserLocator(registry, _ => true, _ => (signed, publisher), Env);
        var location = locator.Locate(BrowserKind.Chrome)!;
        Assert.Equal(expected, location.PublisherVerified);
        Assert.Equal(publisher, location.Publisher);
    }

    [Fact]
    public void PublisherCheckUnavailable_LeavesNull()
    {
        var registry = new FakeRegistry().Set(RegistryRoot.LocalMachine, AppPaths, null, @"C:\c\chrome.exe");
        var locator = new BrowserLocator(registry, _ => true, _ => null, Env);
        Assert.Null(locator.Locate(BrowserKind.Chrome)!.PublisherVerified);
    }

    [Fact]
    public void EdgeExpectsMicrosoft()
    {
        var registry = new FakeRegistry().Set(RegistryRoot.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\msedge.exe", null, @"C:\e\msedge.exe");
        var locator = new BrowserLocator(registry, _ => true, _ => (true, "Microsoft Corporation"), Env);
        Assert.True(locator.Locate(BrowserKind.Edge)!.PublisherVerified);
        Assert.Equal("Microsoft Corporation", BrowserLocator.ExpectedPublisher(BrowserKind.Edge));
        Assert.Equal("Google LLC", BrowserLocator.ExpectedPublisher(BrowserKind.Chrome));
    }

    [Theory]
    [InlineData("CN=Google LLC, O=Google LLC, L=Mountain View, S=California, C=US", "Google LLC")]
    [InlineData("CN=Microsoft Corporation, O=Microsoft Corporation, L=Redmond, S=Washington, C=US", "Microsoft Corporation")]
    [InlineData("CN=x, O=\"Acme, Inc.\", C=US", "Acme, Inc.")]
    [InlineData("CN=only", null)]
    public void ExtractOrganization_FromDn(string dn, string? expected)
    {
        Assert.Equal(expected, BrowserLocator.ExtractOrganization(dn));
    }

    [Fact]
    public void VerifyPublisher_OnNonWindows_ReturnsNull()
    {
        if (OperatingSystem.IsWindows()) return;
        Assert.Null(BrowserLocator.VerifyPublisher("/bin/ls"));
    }
}
