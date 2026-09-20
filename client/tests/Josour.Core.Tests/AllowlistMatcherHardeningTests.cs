using System.Text;
using Josour.Core.Allowlist;

namespace Josour.Core.Tests;

/// <summary>
/// Hardening the list matcher (week 4). The matcher is the only gate between "a name the guest sent" and "a connection leaving the host's network",
/// so a mistake here is not a behaviour bug but a hole. What these tests prove:
///
/// <list type="number">
///   <item><b>It throws nothing but <see cref="ArgumentException"/>/<see cref="FormatException"/>:</b> any input, whatever it is,
///     ends in a declared refusal rather than an exception that escapes the consumer (EgressPolicy and ProxyRouter catch ArgumentException only).</item>
///   <item><b>It fails closed:</b> what does not normalise is not allowed. There is no input that makes <c>IsAllowed</c> return true for a name
///     that is neither the label itself nor a genuine subdomain of it.</item>
///   <item><b>Lookalikes do not match:</b> Punycode separates the Cyrillic and Greek letters from the Latin ones, so
///     <c>раypal.example</c> does not get past an entry of <c>paypal.example</c>.</item>
/// </list>
/// </summary>
public class AllowlistMatcherHardeningTests
{
    private static readonly int[] DefaultPorts = { 80, 443 };

    // ---------- 1. Names that do not normalise: a declared refusal ----------

    [Theory]
    // Empty labels
    [InlineData("a..b")]
    [InlineData(".example.com")]
    [InlineData("..")]
    [InlineData(".")]
    // A hyphen at a label's edge (invalid in DNS; browsers refuse it too)
    [InlineData("-lead.example")]
    [InlineData("trail-.example")]
    [InlineData("a.-b.example")]
    [InlineData("a.b-.example")]
    [InlineData("-")]
    // Characters not permitted in a DNS name, all of which IdnMapping passes through without STD3
    [InlineData("exa mple.com")]
    [InlineData("example.com:443")]
    [InlineData("example.com/path")]
    [InlineData("example.com?a=b")]
    [InlineData("user@example.com")]
    [InlineData("exa\0mple.com")]
    [InlineData("example.com\r\nHost: evil.example")]
    [InlineData("exam\nple.com")]
    [InlineData("[example.com]")]
    [InlineData("exa|mple.com")]
    [InlineData("ex$ample.com")]
    // Empty
    [InlineData("")]
    [InlineData("   ")]
    public void UnrepresentableHosts_AreRejected_WithArgumentException(string host)
    {
        Assert.Throws<ArgumentException>(() => AllowlistMatcher.NormalizeHost(host));
    }

    [Fact]
    public void OverLongLabelAndHost_AreRejected()
    {
        var label = new string('a', AllowlistMatcher.MaxLabelLength);
        Assert.Equal(label + ".example", AllowlistMatcher.NormalizeHost(label + ".example"));
        Assert.Throws<ArgumentException>(() => AllowlistMatcher.NormalizeHost(new string('a', AllowlistMatcher.MaxLabelLength + 1) + ".example"));

        // 4 x 63 + 3 dots = 255 > 253
        var tooLong = string.Join('.', Enumerable.Repeat(label, 4));
        Assert.True(tooLong.Length > AllowlistMatcher.MaxHostLength);
        Assert.Throws<ArgumentException>(() => AllowlistMatcher.NormalizeHost(tooLong));
    }

    [Fact]
    public void PunycodeThatDecodesToSomethingInvalid_IsRejected()
    {
        // xn-- with an invalid payload: GetAscii decodes, re-encodes and complains.
        Assert.Throws<ArgumentException>(() => AllowlistMatcher.NormalizeHost("xn--a-ecp.ru​"));
        Assert.Throws<ArgumentException>(() => AllowlistMatcher.NormalizeHost("xn--é.example"));
    }

    [Fact]
    public void Fuzz_NormalizeHost_OnlyEverThrowsArgumentException()
    {
        var random = new Random(20260905);
        var alphabet = "abcXY01-._:/\\ \t\r\n\0@[]%?#*بمثالاяρ。．｡​­�ÿ";
        for (var i = 0; i < 5000; i++)
        {
            var sb = new StringBuilder();
            for (var n = random.Next(0, 40); n > 0; n--) sb.Append(alphabet[random.Next(alphabet.Length)]);
            var input = sb.ToString();

            string? normalized = null;
            try
            {
                normalized = AllowlistMatcher.NormalizeHost(input);
            }
            catch (ArgumentException)
            {
                continue;
            }
            catch (Exception e)
            {
                Assert.Fail($"unexpected {e.GetType().Name} for {Escape(input)}: {e.Message}");
            }

            // Normalisation succeeded: so the result is lowercase LDH and stable under re-normalisation.
            AssertIsNormalized(normalized!, input);
            Assert.Equal(normalized, AllowlistMatcher.NormalizeHost(normalized!));
        }
    }

    [Fact]
    public void Fuzz_TryParseEntry_NeverThrows_AndAlwaysExplainsItself()
    {
        var random = new Random(11);
        var alphabet = "abc.-=:*/ 019\t\r\n\0مثالя。";
        for (var i = 0; i < 5000; i++)
        {
            var sb = new StringBuilder();
            for (var n = random.Next(0, 24); n > 0; n--) sb.Append(alphabet[random.Next(alphabet.Length)]);
            var raw = sb.ToString();

            bool ok;
            AllowlistEntry? entry;
            string? error;
            try
            {
                ok = AllowlistMatcher.TryParseEntry(raw, out entry, out error);
            }
            catch (Exception e)
            {
                Assert.Fail($"unexpected {e.GetType().Name} for {Escape(raw)}: {e.Message}");
                return;
            }

            if (ok)
            {
                Assert.NotNull(entry);
                AssertIsNormalized(entry!.Host, raw);
                Assert.True(entry.Port is null or (>= 1 and <= 65535));
                // What was accepted as an entry must also get through Parse with no exception.
                Assert.Single(AllowlistMatcher.Parse(1, new[] { raw }).Entries);
            }
            else
            {
                Assert.Null(entry);
                Assert.False(string.IsNullOrEmpty(error));
                Assert.Throws<FormatException>(() => AllowlistMatcher.ParseEntry(raw));
            }
        }
    }

    // ---------- 2. Lookalikes ----------

    [Theory]
    // A Cyrillic 'а' in paypal, a Greek 'ο' in google, a Cyrillic 'е' in secure
    [InlineData("раypal.example")]
    [InlineData("gοogle.example")]
    [InlineData("sеcure.example")]
    // The same trick inside a subdomain
    [InlineData("login.раypal.example")]
    public void Confusables_DoNotMatchTheLatinEntry(string lookalike)
    {
        var matcher = AllowlistMatcher.Parse(1, new[] { "paypal.example", "google.example", "secure.example" });
        var normalized = AllowlistMatcher.NormalizeHost(lookalike);
        // The non-Latin letter becomes an entirely different Punycode label, so there is no suffix and no match.
        Assert.Contains(normalized.Split('.'), label => label.StartsWith("xn--", StringComparison.Ordinal));
        Assert.False(matcher.IsAllowed(normalized, 443, DefaultPorts), normalized);
    }

    [Fact]
    public void Confusables_MatchOnlyTheirOwnPunycodeEntry()
    {
        // An entry written in those same Cyrillic letters does match — and that is the right behaviour: the matching is on Punycode, not on appearance.
        var matcher = AllowlistMatcher.Parse(1, new[] { "раypal.example" });
        Assert.True(matcher.IsAllowed(AllowlistMatcher.NormalizeHost("раypal.example"), 443, DefaultPorts));
        Assert.False(matcher.IsAllowed("paypal.example", 443, DefaultPorts));
    }

    // ---------- 3. Normalisation: what is equal and what is not ----------

    [Theory]
    // The trailing dot (the DNS root) does not change the name
    [InlineData("example.com.", "example.com")]
    [InlineData("example.com...", "example.com")]
    [InlineData("EXAMPLE.COM.", "example.com")]
    // The alternative IDN separators normalise to '.' — correct IDNA behaviour, pinned here so it does not change silently
    [InlineData("example。com", "example.com")]
    [InlineData("example．com", "example.com")]
    [InlineData("example｡com", "example.com")]
    [InlineData("example.com。", "example.com")]
    // Upper/lower case and pre-written Punycode
    [InlineData("XN--BCHER-KVA.DE", "xn--bcher-kva.de")]
    [InlineData("Bücher.DE", "xn--bcher-kva.de")]
    // An underscore is allowed (real DKIM/SRV names)
    [InlineData("_dmarc.example.com", "_dmarc.example.com")]
    public void Normalization_IsCanonical(string input, string expected)
    {
        Assert.Equal(expected, AllowlistMatcher.NormalizeHost(input));
    }

    [Fact]
    public void IdnSeparators_MakeTheSubdomainMatchTheSameEntry()
    {
        // "shop。example.com" is "shop.example.com" in DNS, so it must match an example.com entry — and must not match =exact.
        var matcher = AllowlistMatcher.Parse(1, new[] { "example.com" });
        Assert.True(matcher.IsAllowed(AllowlistMatcher.NormalizeHost("shop。example.com"), 443, DefaultPorts));

        var exact = AllowlistMatcher.Parse(1, new[] { "=example.com" });
        Assert.False(exact.IsAllowed(AllowlistMatcher.NormalizeHost("shop。example.com"), 443, DefaultPorts));
    }

    // ---------- 4. The matching's label boundaries ----------

    [Theory]
    [InlineData("example.com", "example.com", true)]
    [InlineData("example.com", "a.example.com", true)]
    [InlineData("example.com", "a.b.c.example.com", true)]
    [InlineData("example.com", "xexample.com", false)]
    [InlineData("example.com", "x-example.com", false)]
    [InlineData("example.com", "example.com.evil.example", false)]
    [InlineData("example.com", "aexample.com", false)]
    [InlineData("example.com", "_example.com", false)]
    // The last label alone is not enough
    [InlineData("com", "example.com", true)]
    [InlineData("example.com", "com", false)]
    // A dot at the start of the matched name is not a valid boundary (the name is not normalised to begin with)
    [InlineData("example.com", ".example.com", false)]
    public void SuffixMatching_RespectsLabelBoundaries(string entry, string host, bool expected)
    {
        var matcher = AllowlistMatcher.Parse(1, new[] { entry });
        Assert.Equal(expected, matcher.IsAllowed(host, 443, DefaultPorts));
    }

    [Fact]
    public void Property_AllowedImpliesRealLabelSuffix()
    {
        // The property: for every name the matcher accepts, there is an entry that is the name itself or a suffix of it on a label boundary.
        var entries = new[] { "example.com", "=exact.example", "portal.example:8443", "co.uk" };
        var matcher = AllowlistMatcher.Parse(1, entries);
        var random = new Random(3);
        var labels = new[] { "a", "b", "example", "com", "uk", "co", "exact", "portal", "xexample", "com-evil", "0" };

        for (var i = 0; i < 3000; i++)
        {
            var host = string.Join('.', Enumerable.Range(0, random.Next(1, 5)).Select(_ => labels[random.Next(labels.Length)]));
            var port = new[] { 80, 443, 8443, 22, 65535 }[random.Next(5)];
            if (!matcher.IsAllowed(host, port, DefaultPorts)) continue;

            var justified = matcher.Entries.Any(e =>
                host == e.Host || (!e.ExactOnly && host.Length > e.Host.Length && host.EndsWith("." + e.Host, StringComparison.Ordinal)));
            Assert.True(justified, $"{host}:{port} was allowed without a label-boundary entry");
        }
    }

    // ---------- helpers ----------

    private static void AssertIsNormalized(string normalized, string input)
    {
        Assert.False(string.IsNullOrEmpty(normalized), $"empty normalization of {Escape(input)}");
        Assert.True(normalized.Length <= AllowlistMatcher.MaxHostLength, $"{Escape(input)} → {normalized}");
        Assert.DoesNotContain("..", normalized, StringComparison.Ordinal);
        Assert.False(normalized.StartsWith('.') || normalized.EndsWith('.'), $"{Escape(input)} → {normalized}");
        foreach (var label in normalized.Split('.'))
        {
            Assert.InRange(label.Length, 1, AllowlistMatcher.MaxLabelLength);
            Assert.False(label.StartsWith('-') || label.EndsWith('-'), $"{Escape(input)} → {normalized}");
            foreach (var c in label)
            {
                Assert.True(c is >= 'a' and <= 'z' || c is >= '0' and <= '9' || c == '-' || c == '_', $"{Escape(input)} → {normalized} carries '{c}'");
            }
        }
    }

    private static string Escape(string text)
    {
        var sb = new StringBuilder("\"");
        foreach (var c in text) sb.Append(c is >= ' ' and <= '~' ? c.ToString() : $"\\u{(int)c:x4}");
        return sb.Append('"').ToString();
    }
}
