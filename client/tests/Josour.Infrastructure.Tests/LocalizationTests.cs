using System.Globalization;
using System.Text.RegularExpressions;
using Josour.Infrastructure.Localization;

namespace Josour.Infrastructure.Tests;

/// <summary>
/// The localization facade the whole UI binds to: both packs must be complete, agree on their keys and their format
/// placeholders, and the Arabic pack must actually be Arabic — a key left in English is the failure this file exists for.
/// </summary>
public sealed class LocalizationTests
{
    /// <summary>
    /// Keys whose text is a technical identifier or pure punctuation and is therefore the same in both languages:
    /// the product name, a version prefix, the "name · device" joiners and the byte-count format. Everything else must
    /// differ, and must contain Arabic script in the Arabic pack.
    /// </summary>
    private static readonly HashSet<string> IdenticalByDesign = new(StringComparer.Ordinal)
    {
        // AppName, TrayTooltip and TrayTooltipSignedInFormat used to live here: the brand was one
        // Latin word in both packs. The product is now "Josour" in English and "جسور" in Arabic, so
        // those three legitimately differ and are held to the same rule as any other string.
        UiStringKeys.VersionFormat,
        UiStringKeys.StatusSignedInFormat,
        UiStringKeys.SessionPeerFormat,
        UiStringKeys.SessionSummaryFormat,
        UiStringKeys.BytesFormat,
        UiStringKeys.LoginServerUrlPlaceholder,
        UiStringKeys.DebugSampleGuestDevice,

        // Browser names are product names: "Chrome" is Chrome in Arabic too, and translating them would be a bug.
        UiStringKeys.SettingsBrowserChrome,
        UiStringKeys.SettingsBrowserEdge,
    };

    private static readonly Regex Placeholder = new(@"\{(\d+)(?::[^}]*)?\}", RegexOptions.CultureInvariant);

    [Fact]
    public void EveryKey_ResolvesInBothLanguages()
    {
        var arabic = LocalizedStrings.For(UiLanguage.Arabic);
        var english = LocalizedStrings.For(UiLanguage.English);

        Assert.NotEmpty(UiStringKeys.All);
        foreach (var key in UiStringKeys.All)
        {
            Assert.True(arabic.TryGet(key, out var ar), $"The Arabic pack has no '{key}'.");
            Assert.True(english.TryGet(key, out var en), $"The English pack has no '{key}'.");
            Assert.False(string.IsNullOrWhiteSpace(ar), $"The Arabic '{key}' is empty.");
            Assert.False(string.IsNullOrWhiteSpace(en), $"The English '{key}' is empty.");
        }
    }

    [Fact]
    public void NeitherPack_CarriesAKeyTheOtherOneOrUiStringKeysDoesNot()
    {
        var declared = UiStringKeys.All.ToHashSet(StringComparer.Ordinal);
        var arabic = LocalizedStrings.For(UiLanguage.Arabic).Values.Keys.ToHashSet(StringComparer.Ordinal);
        var english = LocalizedStrings.For(UiLanguage.English).Values.Keys.ToHashSet(StringComparer.Ordinal);

        Assert.Equal(declared.Count, UiStringKeys.All.Count); // no duplicate constant
        Assert.Empty(arabic.Except(declared));
        Assert.Empty(english.Except(declared));
        Assert.Empty(declared.Except(arabic));
        Assert.Empty(declared.Except(english));
    }

    [Fact]
    public void NoArabicValue_WasLeftInEnglish()
    {
        var arabic = LocalizedStrings.For(UiLanguage.Arabic);
        var english = LocalizedStrings.For(UiLanguage.English);

        foreach (var key in UiStringKeys.All)
        {
            var ar = arabic.Get(key);
            var en = english.Get(key);

            if (IdenticalByDesign.Contains(key))
            {
                Assert.Equal(en, ar);
                continue;
            }

            Assert.False(string.Equals(ar, en, StringComparison.Ordinal), $"'{key}' is still the English text in the Arabic pack.");
            Assert.True(ar.Any(IsArabicLetter), $"'{key}' carries no Arabic script: {ar}");
        }
    }

    [Fact]
    public void BothPacks_UseTheSameFormatPlaceholders()
    {
        var arabic = LocalizedStrings.For(UiLanguage.Arabic);
        var english = LocalizedStrings.For(UiLanguage.English);

        foreach (var key in UiStringKeys.All)
        {
            var ar = Indices(arabic.Get(key));
            var en = Indices(english.Get(key));

            // Same argument set, in whatever order each language reads best.
            Assert.True(ar.SetEquals(en), $"'{key}' takes {{{string.Join(",", en)}}} in English but {{{string.Join(",", ar)}}} in Arabic.");
        }
    }

    [Fact]
    public void FormatsWithWesternDigits_InBothLanguages()
    {
        // ICU would render an Arabic culture's numbers as ١٫٥; IPs, ports and byte counts must stay copy-pasteable.
        Assert.Equal("1.5 ميجابايت", LocalizedStrings.For(UiLanguage.Arabic).Format(UiStringKeys.BytesFormat, 1.5, "ميجابايت"));
        Assert.Equal("30 دقيقة", LocalizedStrings.For(UiLanguage.Arabic).Format(UiStringKeys.DurationMinutesFormat, 30));
        Assert.Equal("30 minutes", LocalizedStrings.For(UiLanguage.English).Format(UiStringKeys.DurationMinutesFormat, 30));
    }

    [Fact]
    public void ArabicCulture_KeepsArabicTextRulesButInvariantNumbers()
    {
        var arabic = UiLanguage.Arabic.CreateCulture();

        Assert.Equal("ar", arabic.TwoLetterISOLanguageName);
        Assert.Equal(".", arabic.NumberFormat.NumberDecimalSeparator);
        Assert.Equal("1234.5", 1234.5.ToString("0.#", arabic));
        Assert.True(UiLanguage.Arabic.IsRightToLeft());
        Assert.False(UiLanguage.English.IsRightToLeft());
    }

    [Fact]
    public void Ltr_IsolatesTechnicalValuesInArabicOnly()
    {
        var arabic = LocalizedStrings.For(UiLanguage.Arabic);
        var english = LocalizedStrings.For(UiLanguage.English);

        Assert.Equal(
            LocalizedStrings.LeftToRightEmbedding + "192.168.1.20:8443" + LocalizedStrings.PopDirectionalFormatting,
            arabic.Ltr("192.168.1.20:8443"));
        Assert.Equal("192.168.1.20:8443", english.Ltr("192.168.1.20:8443"));
        Assert.Equal(string.Empty, arabic.Ltr(null));
        Assert.Equal(string.Empty, arabic.Ltr(string.Empty));
    }

    [Theory]
    [InlineData("ar", UiLanguage.Arabic)]
    [InlineData("AR", UiLanguage.Arabic)]
    [InlineData(" ar-SA ", UiLanguage.Arabic)]
    [InlineData("arabic", UiLanguage.Arabic)]
    [InlineData("en", UiLanguage.English)]
    [InlineData("en-GB", UiLanguage.English)]
    [InlineData("English", UiLanguage.English)]
    public void TryParse_AcceptsCodesTagsAndNames(string value, UiLanguage expected)
    {
        Assert.True(UiLanguages.TryParse(value, out var language));
        Assert.Equal(expected, language);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("fr")]
    [InlineData("arb")]
    public void TryParse_RejectsAnythingElse_AndFallsBackToArabic(string? value)
    {
        Assert.False(UiLanguages.TryParse(value, out _));
        Assert.Equal(UiLanguage.Arabic, UiLanguages.ParseOrDefault(value));
        Assert.Equal("ar", UiLanguages.Default.ToCode());
    }

    [Fact]
    public void UseLanguage_FixesWhatTheUiReads()
    {
        try
        {
            Assert.Equal(UiLanguage.Arabic, LocalizedStrings.Current.Language); // the default, before anyone chooses

            LocalizedStrings.UseLanguage(UiLanguage.English);
            Assert.Equal("Host", LocalizedStrings.Current[UiStringKeys.HostTab]);

            LocalizedStrings.UseLanguage(UiLanguage.Arabic);
            Assert.Equal("المضيف", LocalizedStrings.Current[UiStringKeys.HostTab]);
        }
        finally
        {
            LocalizedStrings.UseLanguage(UiLanguages.Default);
        }
    }

    [Fact]
    public void UnknownKey_ReturnsTheKeyInsteadOfThrowing()
    {
        Assert.Equal("NotAKey", LocalizedStrings.For(UiLanguage.Arabic).Get("NotAKey"));
        Assert.Throws<ArgumentException>(() => LocalizedStrings.For(UiLanguage.Arabic).Get(" "));
    }

    private static HashSet<string> Indices(string value) =>
        Placeholder.Matches(value).Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);

    /// <summary>The Arabic block (U+0600–U+06FF): enough to tell "translated" from "left in English".</summary>
    private static bool IsArabicLetter(char c) => c is >= '؀' and <= 'ۿ';
}
