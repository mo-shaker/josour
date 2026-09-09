using Josour.Infrastructure.Api;

namespace Josour.Infrastructure.Tests;

/// <summary>
/// The User-Agent is diagnostic decoration, and it once stopped the application from starting.
///
/// <para>Building it from the localized display name produced <c>جسور/0.1.0</c> after the rename.
/// An HTTP header value must be ASCII, so <c>ParseAdd</c> threw inside the DI factory and the
/// **Arabic build - the default one - died at start-up** while the English build was fine. No unit
/// test covered it because the failure lived in the App's service wiring; it surfaced on the first
/// launch of the published exe.</para>
///
/// <para>Two things follow, and both are asserted here: the wire identifier is not the display
/// name, and an unusable User-Agent degrades the header instead of the process.</para>
/// </summary>
public class UserAgentTests
{
    [Fact]
    public void ProductId_IsAsciiAndIndependentOfTheInterfaceLanguage()
    {
        Assert.Equal("Josour", ApiClient.ProductId);
        Assert.All(ApiClient.ProductId, c => Assert.InRange(c, (char)0x21, (char)0x7E));
    }

    [Fact]
    public void TheUserAgentTheAppSendsIsAccepted()
    {
        using var client = ApiClient.CreateHttpClient(
            new HttpClientHandler(), $"{ApiClient.ProductId}/0.1.0");
        Assert.Equal($"{ApiClient.ProductId}/0.1.0", client.DefaultRequestHeaders.UserAgent.ToString());
    }

    [Theory]
    [InlineData("جسور/0.1.0")] // the exact value that shipped broken
    [InlineData("two words/1.0")]
    [InlineData("(unbalanced/1.0")]
    public void AUserAgentTheHeaderParserRejectsDoesNotStopTheClientBeingBuilt(string userAgent)
    {
        using var client = ApiClient.CreateHttpClient(new HttpClientHandler(), userAgent);
        Assert.DoesNotContain(
            "جسور", client.DefaultRequestHeaders.UserAgent.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void NoUserAgentIsAlsoFine()
    {
        using var client = ApiClient.CreateHttpClient(new HttpClientHandler());
        Assert.Empty(client.DefaultRequestHeaders.UserAgent);
    }
}
