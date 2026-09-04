using RouteBridge.Browser.Registry;
using RouteBridge.Core.Browser;

namespace RouteBridge.Browser.Tests;

public class PolicyDetectorTests
{
    [Fact]
    public void NoPolicies_Unmanaged()
    {
        var status = PolicyDetector.Detect(BrowserKind.Chrome, new FakeRegistry());
        Assert.False(status.ProxyManaged);
        Assert.False(status.UserDataDirManaged);
        Assert.False(status.AnyManaged);
        Assert.Empty(status.Findings);
    }

    [Theory]
    [InlineData("ProxyMode", "system")]
    [InlineData("ProxyMode", "fixed_servers")]
    [InlineData("ProxyServer", "proxy.corp:8080")]
    [InlineData("ProxySettings", "{\"ProxyMode\":\"direct\"}")]
    public void AnyProxyValue_InHklm_IsProxyManaged(string name, string value)
    {
        var registry = new FakeRegistry().Set(RegistryRoot.LocalMachine, @"SOFTWARE\Policies\Google\Chrome", name, value);
        var status = PolicyDetector.Detect(BrowserKind.Chrome, registry);
        Assert.True(status.ProxyManaged);
        Assert.False(status.UserDataDirManaged);
        Assert.Contains($"HKLM:{name}={value}", status.Findings);
    }

    [Fact]
    public void UserDataDir_InHkcu_IsUserDataDirManaged()
    {
        var registry = new FakeRegistry().Set(RegistryRoot.CurrentUser, @"SOFTWARE\Policies\Microsoft\Edge", "UserDataDir", @"${roaming_app_data}\Edge");
        var status = PolicyDetector.Detect(BrowserKind.Edge, registry);
        Assert.False(status.ProxyManaged);
        Assert.True(status.UserDataDirManaged);
        Assert.True(status.AnyManaged);
        Assert.Single(status.Findings);
        Assert.StartsWith("HKCU:UserDataDir=", status.Findings[0]);
    }

    [Fact]
    public void ChromePolicies_DoNotAffectEdge()
    {
        var registry = new FakeRegistry().Set(RegistryRoot.LocalMachine, @"SOFTWARE\Policies\Google\Chrome", "ProxyMode", "system");
        Assert.False(PolicyDetector.Detect(BrowserKind.Edge, registry).AnyManaged);
        Assert.True(PolicyDetector.Detect(BrowserKind.Chrome, registry).AnyManaged);
    }

    [Fact]
    public void PolicyKeys_MatchPlan()
    {
        Assert.Equal(@"SOFTWARE\Policies\Google\Chrome", PolicyDetector.PolicyKey(BrowserKind.Chrome));
        Assert.Equal(@"SOFTWARE\Policies\Microsoft\Edge", PolicyDetector.PolicyKey(BrowserKind.Edge));
    }

    [Fact]
    public void NullRegistry_Unmanaged()
    {
        Assert.False(PolicyDetector.Detect(BrowserKind.Chrome, NullRegistryReader.Instance).AnyManaged);
    }
}
