using RouteBridge.Core.Allowlist;

namespace RouteBridge.Core.Tests;

public class AllowlistMatcherTests
{
    private static readonly int[] DefaultPorts = { 80, 443 };

    [Theory]
    [InlineData("example.com", "example.com", true)]
    [InlineData("example.com", "www.example.com", true)]
    [InlineData("example.com", "a.b.example.com", true)]
    [InlineData("example.com", "notexample.com", false)]
    [InlineData("example.com", "example.com.evil", false)]
    [InlineData("example.com", "example.org", false)]
    [InlineData("example.com", "com", false)]
    [InlineData("=exact.com", "exact.com", true)]
    [InlineData("=exact.com", "www.exact.com", false)]
    [InlineData("=exact.com", "exact.com.evil", false)]
    public void HostMatching(string entry, string host, bool expected)
    {
        var matcher = AllowlistMatcher.Parse(1, new[] { entry });
        Assert.Equal(expected, matcher.IsAllowed(host, 443, DefaultPorts));
    }

    [Theory]
    [InlineData("example.com", "example.com", 443, true)]
    [InlineData("example.com", "example.com", 80, true)]
    [InlineData("example.com", "www.example.com", 8443, false)]
    [InlineData("example.com:8443", "example.com", 8443, true)]
    [InlineData("example.com:8443", "www.example.com", 8443, true)]
    [InlineData("example.com:8443", "example.com", 443, false)]
    [InlineData("=exact.com:8080", "exact.com", 8080, true)]
    [InlineData("=exact.com:8080", "exact.com", 443, false)]
    [InlineData("=exact.com:8080", "www.exact.com", 8080, false)]
    public void PortRules(string entry, string host, int port, bool expected)
    {
        var matcher = AllowlistMatcher.Parse(1, new[] { entry });
        Assert.Equal(expected, matcher.IsAllowed(host, port, DefaultPorts));
    }

    [Fact]
    public void Evaluate_DistinguishesHostAndPortFailures()
    {
        var matcher = AllowlistMatcher.Parse(1, new[] { "example.com", "api.example.net:8443" });
        Assert.Equal(AllowlistDecision.Allowed, matcher.Evaluate("example.com", 443, DefaultPorts));
        Assert.Equal(AllowlistDecision.PortNotAllowed, matcher.Evaluate("example.com", 9999, DefaultPorts));
        Assert.Equal(AllowlistDecision.HostNotAllowed, matcher.Evaluate("evil.com", 443, DefaultPorts));
        Assert.Equal(AllowlistDecision.Allowed, matcher.Evaluate("api.example.net", 8443, DefaultPorts));
        Assert.Equal(AllowlistDecision.PortNotAllowed, matcher.Evaluate("api.example.net", 443, DefaultPorts));
    }

    [Fact]
    public void MultipleEntries_AnyMatchingEntryAllows()
    {
        var matcher = AllowlistMatcher.Parse(1, new[] { "example.com:8443", "example.com" });
        Assert.True(matcher.IsAllowed("example.com", 443, DefaultPorts));
        Assert.True(matcher.IsAllowed("example.com", 8443, DefaultPorts));
        Assert.False(matcher.IsAllowed("example.com", 9999, DefaultPorts));
    }

    [Fact]
    public void AllowedPorts_AreHonouredForEntriesWithoutPort()
    {
        var matcher = AllowlistMatcher.Parse(1, new[] { "example.com" });
        Assert.True(matcher.IsAllowed("example.com", 8080, new[] { 8080 }));
        Assert.False(matcher.IsAllowed("example.com", 443, new[] { 8080 }));
        Assert.False(matcher.IsAllowed("example.com", 443, Array.Empty<int>()));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("*")]
    [InlineData("*.example.com")]
    [InlineData("example.com/path")]
    [InlineData("exa mple.com")]
    [InlineData("example.com ")]
    [InlineData("example.com\t")]
    [InlineData("example.com:0")]
    [InlineData("example.com:65536")]
    [InlineData("example.com:abc")]
    [InlineData("example.com:")]
    [InlineData("=")]
    [InlineData(":443")]
    [InlineData("=:443")]
    [InlineData("a:1:2")]
    [InlineData("a..b")]
    [InlineData(".")]
    public void BadEntries_AreRejectedAtLoad(string raw)
    {
        Assert.False(AllowlistMatcher.TryParseEntry(raw, out var entry, out var error));
        Assert.Null(entry);
        Assert.False(string.IsNullOrEmpty(error));
        Assert.Throws<FormatException>(() => AllowlistMatcher.ParseEntry(raw));
        Assert.Throws<FormatException>(() => AllowlistMatcher.Parse(1, new[] { "ok.com", raw }));
    }

    [Fact]
    public void NullEntry_IsRejected()
    {
        Assert.False(AllowlistMatcher.TryParseEntry(null, out _, out var error));
        Assert.NotNull(error);
    }

    [Theory]
    [InlineData("example.com", "example.com", false, null)]
    [InlineData("EXAMPLE.com", "example.com", false, null)]
    [InlineData("example.com.", "example.com", false, null)]
    [InlineData("=Exact.COM.", "exact.com", true, null)]
    [InlineData("bücher.de", "xn--bcher-kva.de", false, null)]
    [InlineData("=Bücher.DE:8443", "xn--bcher-kva.de", true, 8443)]
    [InlineData("xn--bcher-kva.de", "xn--bcher-kva.de", false, null)]
    [InlineData("example.com:65535", "example.com", false, 65535)]
    [InlineData("example.com:1", "example.com", false, 1)]
    public void Parse_NormalizesEntries(string raw, string host, bool exact, int? port)
    {
        var entry = AllowlistMatcher.ParseEntry(raw);
        Assert.Equal(new AllowlistEntry(host, exact, port), entry);
    }

    [Theory]
    [InlineData("WWW.Example.COM.", "www.example.com")]
    [InlineData("bücher.example", "xn--bcher-kva.example")]
    [InlineData("  a.b  ", "a.b")]
    [InlineData("xn--bcher-kva.de", "xn--bcher-kva.de")]
    [InlineData("مثال.إختبار", "xn--mgbh0fb.xn--kgbechtv")]
    public void NormalizeHost_LowercasesStripsDotAndPunycodes(string input, string expected)
    {
        Assert.Equal(expected, AllowlistMatcher.NormalizeHost(input));
    }

    [Theory]
    [InlineData("")]
    [InlineData(".")]
    [InlineData("   ")]
    [InlineData("a..b")]
    public void NormalizeHost_InvalidInput_Throws(string input)
    {
        Assert.Throws<ArgumentException>(() => AllowlistMatcher.NormalizeHost(input));
    }

    [Fact]
    public void PunycodeEntry_MatchesPunycodeHost()
    {
        var matcher = AllowlistMatcher.Parse(1, new[] { "bücher.de" });
        Assert.True(matcher.IsAllowed("xn--bcher-kva.de", 443, DefaultPorts));
        Assert.True(matcher.IsAllowed("shop.xn--bcher-kva.de", 443, DefaultPorts));
    }

    [Fact]
    public void IsAllowed_FailsClosedForUnnormalizedHost()
    {
        // العقد: الاسم مطبَّع قبل الاستدعاء. اسم غير مطبَّع لا يطابق (لا يُمنح صلاحية بالخطأ).
        var matcher = AllowlistMatcher.Parse(1, new[] { "example.com" });
        Assert.False(matcher.IsAllowed("EXAMPLE.COM", 443, DefaultPorts));
        Assert.False(matcher.IsAllowed("example.com.", 443, DefaultPorts));
    }

    [Fact]
    public void VersionAndEntries_AreExposed()
    {
        var matcher = AllowlistMatcher.Parse(7, new[] { "a.com", "=b.com:8443" });
        Assert.Equal(7, matcher.Version);
        Assert.Equal(2, matcher.Entries.Count);
        Assert.Equal(new AllowlistEntry("a.com", false, null), matcher.Entries[0]);
        Assert.Equal(new AllowlistEntry("b.com", true, 8443), matcher.Entries[1]);
    }

    [Fact]
    public void EmptyAllowlist_AllowsNothing()
    {
        var matcher = AllowlistMatcher.Parse(1, Array.Empty<string>());
        Assert.Equal(AllowlistDecision.HostNotAllowed, matcher.Evaluate("example.com", 443, DefaultPorts));
    }
}
