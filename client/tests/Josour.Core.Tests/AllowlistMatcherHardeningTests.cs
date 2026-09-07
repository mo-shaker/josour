using System.Text;
using Josour.Core.Allowlist;

namespace Josour.Core.Tests;

/// <summary>
/// تقوية مطابق القائمة (الأسبوع 4). المطابق هو البوابة الوحيدة بين "اسم أرسله الضيف" و"اتصال يخرج من شبكة المضيف"،
/// فخطأ هنا ليس خطأ سلوك بل ثغرة. ما تثبته هذه الاختبارات:
///
/// <list type="number">
///   <item><b>لا يرمي إلا <see cref="ArgumentException"/>/<see cref="FormatException"/>:</b> أي مدخل، مهما كان،
///     ينتهي إلى رفض معلن لا إلى استثناء يخترق المستهلك (EgressPolicy وProxyRouter يمسكان ArgumentException فقط).</item>
///   <item><b>يفشل مغلقًا:</b> ما لا يُطبَّع لا يُسمح به. لا يوجد مدخل يجعل <c>IsAllowed</c> تعيد true لاسم
///     ليس التسمية نفسها أو نطاقًا فرعيًا حقيقيًا منها.</item>
///   <item><b>المتشابهات لا تطابق:</b> Punycode يفصل الحروف السيريلية والإغريقية عن اللاتينية، فلا يمر
///     <c>раypal.example</c> على مدخل <c>paypal.example</c>.</item>
/// </list>
/// </summary>
public class AllowlistMatcherHardeningTests
{
    private static readonly int[] DefaultPorts = { 80, 443 };

    // ---------- 1. أسماء لا تُطبَّع: رفض معلن ----------

    [Theory]
    // تسميات فارغة
    [InlineData("a..b")]
    [InlineData(".example.com")]
    [InlineData("..")]
    [InlineData(".")]
    // شرطة في طرف التسمية (غير صالحة في DNS؛ متصفحات ترفضها كذلك)
    [InlineData("-lead.example")]
    [InlineData("trail-.example")]
    [InlineData("a.-b.example")]
    [InlineData("a.b-.example")]
    [InlineData("-")]
    // محارف لا تجوز في اسم DNS، وكلها يمررها IdnMapping بلا STD3
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
    // فارغ
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

        // 4 × 63 + 3 نقاط = 255 > 253
        var tooLong = string.Join('.', Enumerable.Repeat(label, 4));
        Assert.True(tooLong.Length > AllowlistMatcher.MaxHostLength);
        Assert.Throws<ArgumentException>(() => AllowlistMatcher.NormalizeHost(tooLong));
    }

    [Fact]
    public void PunycodeThatDecodesToSomethingInvalid_IsRejected()
    {
        // xn-- مع حمولة غير صالحة: GetAscii يفك ويعيد الترميز ويشكو.
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

            // نجح التطبيع: فالناتج LDH صغير الحروف ومستقر عند إعادة التطبيع.
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
                // ما قُبل كمدخل يجب أن يمر أيضًا عبر Parse بلا استثناء.
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

    // ---------- 2. المتشابهات ----------

    [Theory]
    // 'а' سيريلية في paypal، 'ο' إغريقية في google، 'е' سيريلية في secure
    [InlineData("раypal.example")]
    [InlineData("gοogle.example")]
    [InlineData("sеcure.example")]
    // نفس الحيلة داخل نطاق فرعي
    [InlineData("login.раypal.example")]
    public void Confusables_DoNotMatchTheLatinEntry(string lookalike)
    {
        var matcher = AllowlistMatcher.Parse(1, new[] { "paypal.example", "google.example", "secure.example" });
        var normalized = AllowlistMatcher.NormalizeHost(lookalike);
        // الحرف غير اللاتيني يصير تسمية Punycode مختلفة تمامًا، فلا لاحقة ولا تطابق.
        Assert.Contains(normalized.Split('.'), label => label.StartsWith("xn--", StringComparison.Ordinal));
        Assert.False(matcher.IsAllowed(normalized, 443, DefaultPorts), normalized);
    }

    [Fact]
    public void Confusables_MatchOnlyTheirOwnPunycodeEntry()
    {
        // المدخل المكتوب بالحروف السيريلية نفسها يطابق — وهذا هو السلوك الصحيح: المطابقة على Punycode لا على الشكل.
        var matcher = AllowlistMatcher.Parse(1, new[] { "раypal.example" });
        Assert.True(matcher.IsAllowed(AllowlistMatcher.NormalizeHost("раypal.example"), 443, DefaultPorts));
        Assert.False(matcher.IsAllowed("paypal.example", 443, DefaultPorts));
    }

    // ---------- 3. التطبيع: ما يتساوى وما لا يتساوى ----------

    [Theory]
    // النقطة الأخيرة (جذر DNS) لا تغيّر الاسم
    [InlineData("example.com.", "example.com")]
    [InlineData("example.com...", "example.com")]
    [InlineData("EXAMPLE.COM.", "example.com")]
    // فواصل IDN البديلة تُطبَّع إلى '.' — سلوك IDNA الصحيح، مثبَّت هنا حتى لا يتغير بصمت
    [InlineData("example。com", "example.com")]
    [InlineData("example．com", "example.com")]
    [InlineData("example｡com", "example.com")]
    [InlineData("example.com。", "example.com")]
    // كبير/صغير وPunycode المكتوب مسبقًا
    [InlineData("XN--BCHER-KVA.DE", "xn--bcher-kva.de")]
    [InlineData("Bücher.DE", "xn--bcher-kva.de")]
    // شرطة سفلية مسموحة (أسماء DKIM/SRV الحقيقية)
    [InlineData("_dmarc.example.com", "_dmarc.example.com")]
    public void Normalization_IsCanonical(string input, string expected)
    {
        Assert.Equal(expected, AllowlistMatcher.NormalizeHost(input));
    }

    [Fact]
    public void IdnSeparators_MakeTheSubdomainMatchTheSameEntry()
    {
        // ‏"shop。example.com" هو "shop.example.com" في DNS، فيجب أن يطابق مدخل example.com — ولا يجوز أن يطابق =exact.
        var matcher = AllowlistMatcher.Parse(1, new[] { "example.com" });
        Assert.True(matcher.IsAllowed(AllowlistMatcher.NormalizeHost("shop。example.com"), 443, DefaultPorts));

        var exact = AllowlistMatcher.Parse(1, new[] { "=example.com" });
        Assert.False(exact.IsAllowed(AllowlistMatcher.NormalizeHost("shop。example.com"), 443, DefaultPorts));
    }

    // ---------- 4. حدود المطابقة على التسميات ----------

    [Theory]
    [InlineData("example.com", "example.com", true)]
    [InlineData("example.com", "a.example.com", true)]
    [InlineData("example.com", "a.b.c.example.com", true)]
    [InlineData("example.com", "xexample.com", false)]
    [InlineData("example.com", "x-example.com", false)]
    [InlineData("example.com", "example.com.evil.example", false)]
    [InlineData("example.com", "aexample.com", false)]
    [InlineData("example.com", "_example.com", false)]
    // التسمية الأخيرة وحدها لا تكفي
    [InlineData("com", "example.com", true)]
    [InlineData("example.com", "com", false)]
    // نقطة في أول الاسم المطابَق ليست حدودًا صالحة (الاسم غير مطبَّع أصلًا)
    [InlineData("example.com", ".example.com", false)]
    public void SuffixMatching_RespectsLabelBoundaries(string entry, string host, bool expected)
    {
        var matcher = AllowlistMatcher.Parse(1, new[] { entry });
        Assert.Equal(expected, matcher.IsAllowed(host, 443, DefaultPorts));
    }

    [Fact]
    public void Property_AllowedImpliesRealLabelSuffix()
    {
        // الخاصية: لكل اسم يقبله المطابق، يوجد مدخل هو الاسم نفسه أو لاحقة له على حدّ تسمية.
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
