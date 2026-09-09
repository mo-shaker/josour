using Josour.Infrastructure.Settings;

namespace Josour.Infrastructure.Tests;

/// <summary>
/// Where a start-up lands. The branch week 6 added is the important one: a machine with no server address never reaches
/// the sign-in window, because that window would ask for an address it never explains.
/// </summary>
public sealed class StartupPlanTests
{
    private static AppSettings With(string serverUrl) => new() { ServerUrl = serverUrl };

    [Fact]
    public void AFreshMachine_GoesThroughTheGuidedFirstRun()
    {
        var plan = StartupPlanner.Decide(AppSettings.Default, credentialsStored: false, sessionRestored: false);

        Assert.Equal(StartupDestination.FirstRun, plan.Destination);
        Assert.True(plan.IsFreshInstall);
        Assert.Equal(string.Empty, plan.ServerUrl);
    }

    [Fact]
    public void NoSettingsFileAtAll_IsTreatedTheSameWay()
    {
        var plan = StartupPlanner.Decide(null, credentialsStored: false, sessionRestored: false);

        Assert.Equal(StartupDestination.FirstRun, plan.Destination);
        Assert.True(plan.IsFreshInstall);
    }

    [Fact]
    public void AKnownServerWithoutASession_GoesStraightToSignIn_WithTheAddressFilledIn()
    {
        var plan = StartupPlanner.Decide(With("https://josour.example.com"), credentialsStored: true, sessionRestored: false);

        Assert.Equal(StartupDestination.SignIn, plan.Destination);
        Assert.False(plan.IsFreshInstall);
        Assert.Equal("https://josour.example.com", plan.ServerUrl);
    }

    [Fact]
    public void AUserWhoSignedOut_IsNotSentThroughTheFirstRunAgain()
    {
        // Signing out clears the credentials but keeps the address: they know their server, they just need to sign in.
        var plan = StartupPlanner.Decide(With("https://josour.example.com"), credentialsStored: false, sessionRestored: false);

        Assert.Equal(StartupDestination.SignIn, plan.Destination);
        Assert.False(plan.IsFreshInstall);
    }

    [Fact]
    public void ARestoredSession_GoesToTheMainWindow()
    {
        var plan = StartupPlanner.Decide(With("https://josour.example.com"), credentialsStored: true, sessionRestored: true);

        Assert.Equal(StartupDestination.MainWindow, plan.Destination);
        Assert.Equal("https://josour.example.com", plan.ServerUrl);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("josour.example.com")]
    [InlineData("http://josour.example.com")] // plain http to a remote host is refused everywhere else too
    [InlineData("https://josour.example.com/?x=1")]
    public void AnAddressTheApiClientWouldRefuse_CountsAsNoAddress(string serverUrl)
    {
        // Otherwise the sign-in window would open on a value that cannot work, and fail at the first request.
        var plan = StartupPlanner.Decide(With(serverUrl), credentialsStored: true, sessionRestored: false);

        Assert.Equal(StartupDestination.FirstRun, plan.Destination);
        Assert.False(plan.IsFreshInstall); // credentials exist, so this is a broken install, not a new one
    }

    [Fact]
    public void TheAddressIsNormalized_SoTheWindowShowsWhatWillBeUsed()
    {
        var plan = StartupPlanner.Decide(With("  https://Josour.Example.com/  "), credentialsStored: true, sessionRestored: false);

        Assert.Equal("https://josour.example.com", plan.ServerUrl);
    }
}
