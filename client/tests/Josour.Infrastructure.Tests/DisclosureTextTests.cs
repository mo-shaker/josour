using Josour.Infrastructure.Localization;
using Josour.Infrastructure.Session;

namespace Josour.Infrastructure.Tests;

/// <summary>
/// The host's consent is these three strings. Before ADR-0010 the note under the heading was fixed text
/// saying only the company list could be reached through this device; after ADR-0010 that was false, and
/// it sat directly above the line saying the guest could reach anything — two opposite claims in one
/// window, the false one reassuring. These pin the three states apart so nothing merges them again.
/// </summary>
public sealed class DisclosureTextTests
{
    private static readonly string[] TwoSites = ["portal.example.com", "mail.example.com"];

    [Fact]
    public void NoRestriction_DoesNotTellTheHostAListLimitsAnything()
    {
        var disclosure = AllowlistDisclosure.NoRestriction(version: 3);

        Assert.Equal(string.Empty, DisclosureText.Note(disclosure));
        Assert.Equal(LocalizedStrings.Current[UiStringKeys.AllowedSitesUnrestricted], DisclosureText.Message(disclosure));
    }

    [Fact]
    public void NoRestriction_HeadingNamesTheScope_NotAList()
    {
        var disclosure = AllowlistDisclosure.NoRestriction(version: 3);

        Assert.Equal(LocalizedStrings.Current[UiStringKeys.IncomingRequestScopeLabel], DisclosureText.Label(disclosure));
        Assert.NotEqual(LocalizedStrings.Current[UiStringKeys.IncomingRequestAllowedSitesLabel], DisclosureText.Label(disclosure));
    }

    [Fact]
    public void WithAList_TheHeadingAndTheNoteAreBothAboutThatList()
    {
        var disclosure = new AllowlistDisclosure(2, TwoSites, Loaded: true);

        Assert.Equal(LocalizedStrings.Current[UiStringKeys.IncomingRequestAllowedSitesLabel], DisclosureText.Label(disclosure));
        Assert.Equal(LocalizedStrings.Current[UiStringKeys.AllowedSitesNote], DisclosureText.Note(disclosure));
        Assert.Equal(string.Empty, DisclosureText.Message(disclosure));
    }

    [Fact]
    public void AListThatCouldNotBeLoaded_NeverLooksLikeAListThatAllowsNothing()
    {
        var unavailable = DisclosureText.Message(AllowlistDisclosure.Unavailable(version: 2));
        var empty = DisclosureText.Message(new AllowlistDisclosure(2, [], Loaded: true));

        Assert.NotEmpty(unavailable);
        Assert.NotEmpty(empty);
        Assert.NotEqual(unavailable, empty);
    }

    [Fact]
    public void NoRestriction_IsNotReportedAsAnEmptyList()
    {
        var noRestriction = AllowlistDisclosure.NoRestriction(version: 3);

        Assert.False(noRestriction.IsEmpty);
        Assert.NotEqual(DisclosureText.Message(new AllowlistDisclosure(3, [], Loaded: true)), DisclosureText.Message(noRestriction));
    }

    [Theory]
    [InlineData(UiLanguage.Arabic)]
    [InlineData(UiLanguage.English)]
    public void TheScopeHeadingAndTheUnrestrictedSentence_ReadInBothLanguages(UiLanguage language)
    {
        var strings = LocalizedStrings.For(language);
        var disclosure = AllowlistDisclosure.NoRestriction(version: 3);

        // A key missing from the resources comes back as the key itself, so comparing against it catches that.
        Assert.NotEqual(UiStringKeys.IncomingRequestScopeLabel, DisclosureText.Label(disclosure, strings));
        Assert.NotEqual(UiStringKeys.AllowedSitesUnrestricted, DisclosureText.Message(disclosure, strings));
        Assert.Equal(string.Empty, DisclosureText.Note(disclosure, strings));
    }
}
