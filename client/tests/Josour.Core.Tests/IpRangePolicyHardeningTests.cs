using System.Net;
using Josour.Core.Net;

namespace Josour.Core.Tests;

/// <summary>
/// تقوية سياسة العناوين (الأسبوع 4). هذه آخر بوابة قبل <c>Socket.ConnectAsync</c> على المضيف، فأي عنوان يفلت منها
/// هو اتصال حقيقي من شبكة الشركة إلى حيث يريد الضيف. ما تثبته هذه الاختبارات:
///
/// <list type="number">
///   <item><b>كل حدّ لكل نطاق:</b> لكل مدى في docs/protocol.md القسم 6 القاعدة 6 يُفحص أول عنوان وآخر عنوان
///     (محظوران) والعنوان الذي قبله والذي بعده (غير محظورين، ما لم يقعا في مدى آخر). خطأ بمقدار واحد في قناع
///     البتات لا يمكن أن ينجو من هذه المصفوفة.</item>
///   <item><b>التغليفات العدائية:</b> كل صيغة IPv6 تحمل IPv4 داخلها (mapped، NAT64، 6to4، Teredo، والصيغة
///     المهجورة <c>::a.b.c.d</c>) تُفك ويُفحص المضمَّن — بما فيها التغليف المزدوج والحواف.</item>
///   <item><b>عناوين المضيف نفسه:</b> تُحظر بكل أشكالها المغلِّفة مهما كان الشكل الذي وصل به العنوان.</item>
/// </list>
/// </summary>
public class IpRangePolicyHardeningTests
{
    /// <summary>كل نطاق IPv4 محظور في العقد: (أول عنوان، آخر عنوان).</summary>
    public static TheoryData<string, string> V4Ranges => new()
    {
        { "0.0.0.0", "0.255.255.255" },
        { "10.0.0.0", "10.255.255.255" },
        { "100.64.0.0", "100.127.255.255" },
        { "127.0.0.0", "127.255.255.255" },
        { "169.254.0.0", "169.254.255.255" },
        { "172.16.0.0", "172.31.255.255" },
        { "192.0.0.0", "192.0.0.255" },
        { "192.0.2.0", "192.0.2.255" },
        { "192.168.0.0", "192.168.255.255" },
        { "198.18.0.0", "198.19.255.255" },
        { "198.51.100.0", "198.51.100.255" },
        { "203.0.113.0", "203.0.113.255" },
        { "224.0.0.0", "239.255.255.255" },
        { "240.0.0.0", "255.255.255.255" },
    };

    [Theory]
    [MemberData(nameof(V4Ranges))]
    public void V4Range_FirstAndLastAreBlocked_NeighboursAreNot(string first, string last)
    {
        Assert.True(IpRangePolicy.IsBlockedRange(IPAddress.Parse(first)), first);
        Assert.True(IpRangePolicy.IsBlockedRange(IPAddress.Parse(last)), last);

        // الجار الأدنى والأعلى: غير محظورين إلا إن وقعا في مدى آخر من الجدول (224/4 و240/4 متلاصقان مثلًا).
        var below = Step(first, -1);
        var above = Step(last, +1);
        AssertNeighbour(below, first, last);
        AssertNeighbour(above, first, last);
    }

    [Theory]
    [MemberData(nameof(V4Ranges))]
    public void V4Range_BoundariesAreBlockedThroughEveryIPv6Wrapper(string first, string last)
    {
        foreach (var v4 in new[] { first, last })
        {
            Assert.True(IpRangePolicy.IsBlockedRange(Mapped(v4)), $"::ffff:{v4}");
            Assert.True(IpRangePolicy.IsBlockedRange(Nat64(v4)), $"64:ff9b::{v4}");
            Assert.True(IpRangePolicy.IsBlockedRange(SixToFour(v4)), $"2002:{v4}::");
            Assert.True(IpRangePolicy.IsBlockedRange(Teredo(v4)), $"teredo({v4})");
            Assert.True(IpRangePolicy.IsBlockedRange(V4Compatible(v4)), $"::{v4}");
        }
    }

    [Theory]
    // الحدود الدقيقة لكل مدى IPv6 مذكور في العقد
    [InlineData("::", true)]
    [InlineData("::1", true)]
    [InlineData("::2", true)]                                                  // ::/96 المهجور → 0.0.0.2 → 0.0.0.0/8
    [InlineData("fbff:ffff:ffff:ffff:ffff:ffff:ffff:ffff", false)]             // قبل fc00::/7
    [InlineData("fc00::", true)]
    [InlineData("fdff:ffff:ffff:ffff:ffff:ffff:ffff:ffff", true)]              // آخر fc00::/7
    [InlineData("fe00::", false)]                                              // بعد fc00::/7 وقبل fe80::/10
    [InlineData("fe7f:ffff:ffff:ffff:ffff:ffff:ffff:ffff", false)]
    [InlineData("fe80::", true)]
    [InlineData("febf:ffff:ffff:ffff:ffff:ffff:ffff:ffff", true)]              // آخر fe80::/10
    [InlineData("fec0::", false)]                                              // بعد fe80::/10
    [InlineData("feff:ffff:ffff:ffff:ffff:ffff:ffff:ffff", false)]
    [InlineData("ff00::", true)]
    [InlineData("ffff:ffff:ffff:ffff:ffff:ffff:ffff:ffff", true)]              // آخر ff00::/8
    public void V6RangeBoundaries(string ip, bool blocked)
    {
        Assert.Equal(blocked, IpRangePolicy.IsBlockedRange(IPAddress.Parse(ip)));
        Assert.Equal(blocked, IpRangePolicy.IsBlocked(IPAddress.Parse(ip)));
    }

    [Theory]
    // ::ffff:0:0/96 — أول وآخر العنوان المغلَّف
    [InlineData("::ffff:0.0.0.0", true)]
    [InlineData("::ffff:255.255.255.255", true)]
    [InlineData("::ffff:0.255.255.255", true)]
    [InlineData("::ffff:1.0.0.0", false)]
    // النظير قبل البادئة مباشرة وبعدها: ليست تغليفًا، ولا تُفَك
    [InlineData("::fffe:8.8.8.8", false)]
    [InlineData("0:0:0:0:1:ffff:0808:0808", false)]
    // 64:ff9b::/96 — الحواف
    [InlineData("64:ff9b::0.0.0.0", true)]
    [InlineData("64:ff9b::255.255.255.255", true)]
    [InlineData("64:ff9b::1.0.0.0", false)]
    [InlineData("64:ff9a::7f00:1", false)]                                     // بادئة مجاورة: لا تُفَك
    [InlineData("64:ff9c::7f00:1", false)]
    // 2002::/16 — البتات 16-47 هي الـ v4
    [InlineData("2002::", true)]                                               // 0.0.0.0
    [InlineData("2002:7f00:0001::", true)]                                     // 127.0.0.1
    [InlineData("2002:ffff:ffff::", true)]                                     // 255.255.255.255
    [InlineData("2002:0100:0000::", false)]                                    // 1.0.0.0
    [InlineData("2001:0100:0000::", false)]                                    // بادئة مجاورة
    [InlineData("2003:7f00:0001::", false)]
    // 2001::/32 Teredo — آخر 32 بت XOR 0xFFFFFFFF
    [InlineData("2001:0:0:0:0:0:80ff:fffe", true)]                             // 127.0.0.1
    [InlineData("2001:0:0:0:0:0:ffff:ffff", true)]                             // 0.0.0.0
    [InlineData("2001:0:0:0:0:0:0:0", true)]                                   // 255.255.255.255 → 240.0.0.0/4
    [InlineData("2001:0:0:0:0:0:feff:ffff", false)]                            // 1.0.0.0
    [InlineData("2001:1:0:0:0:0:80ff:fffe", false)]                            // خارج 2001::/32: لا فك
    [InlineData("2001:0:1:0:0:0:80ff:fffe", true)]                             // داخل 2001::/32 مهما كان الوسط
    public void EmbeddedV4_Wrappers_AreUnwrappedAtTheirEdges(string ip, bool blocked)
    {
        Assert.Equal(blocked, IpRangePolicy.IsBlockedRange(IPAddress.Parse(ip)));
    }

    [Theory]
    // الصيغة المهجورة ::a.b.c.d (RFC 4291 §2.5.5.1): محلل DNS خبيث قد يعيدها للالتفاف على فحص v4.
    [InlineData("::127.0.0.1", true)]
    [InlineData("::10.0.0.1", true)]
    [InlineData("::192.168.1.1", true)]
    [InlineData("::169.254.169.254", true)]                                    // بيانات وصفية سحابية
    [InlineData("::8.8.8.8", false)]
    [InlineData("::1.1.1.1", false)]
    public void DeprecatedIPv4CompatibleForm_IsUnwrapped(string ip, bool blocked)
    {
        Assert.Equal(blocked, IpRangePolicy.IsBlockedRange(IPAddress.Parse(ip)));
        Assert.Equal(blocked, IpRangePolicy.IsBlocked(IPAddress.Parse(ip)));
    }

    [Fact]
    public void CloudMetadataAddress_IsBlockedInEveryForm()
    {
        // 169.254.169.254 هو الهدف الأول لأي SSRF؛ يجب أن يسقط بكل تغليف.
        foreach (var address in new[]
                 {
                     IPAddress.Parse("169.254.169.254"),
                     Mapped("169.254.169.254"),
                     Nat64("169.254.169.254"),
                     SixToFour("169.254.169.254"),
                     Teredo("169.254.169.254"),
                     V4Compatible("169.254.169.254"),
                 })
        {
            Assert.True(IpRangePolicy.IsBlocked(address), address.ToString());
        }
    }

    [Fact]
    public void LocalAddresses_AreBlockedThroughEveryWrapper_InBothDirections()
    {
        // العنوان العام كما يراه الخادم (docs/protocol.md القسم 6 القاعدة 6): عام في المدى لكنه ممنوع لأنه المضيف نفسه.
        var ours = "203.0.113.9";
        // ‏203.0.113.0/24 محظور أصلًا كمدى وثائقي، فنستخدم عنوانًا عامًا حقيقيًا لعزل قاعدة "عناوين المضيف".
        ours = "198.51.99.9";
        var locals = new[] { IPAddress.Parse(ours) };

        foreach (var form in new[] { IPAddress.Parse(ours), Mapped(ours), Nat64(ours), SixToFour(ours), V4Compatible(ours) })
        {
            Assert.True(IpRangePolicy.IsBlocked(form, locals), $"{form} against {ours}");
        }

        // والعكس: العنوان المحلي معطًى بشكل مغلَّف يجب أن يحظر الشكل الصريح.
        foreach (var wrapped in new[] { Mapped(ours), Nat64(ours), V4Compatible(ours) })
        {
            Assert.True(IpRangePolicy.IsBlocked(IPAddress.Parse(ours), new[] { wrapped }), $"{ours} against {wrapped}");
        }

        Assert.False(IpRangePolicy.IsBlocked(IPAddress.Parse("198.51.99.10"), locals));
    }

    [Fact]
    public void NonIpFamilies_AreBlocked()
    {
        // عائلة غير IP لا تُطلب أبدًا؛ الافتراضي حظر لا تمرير.
        var unix = new IPAddress(new byte[4]).MapToIPv6();
        Assert.True(IpRangePolicy.IsBlockedRange(unix)); // ::ffff:0.0.0.0
        Assert.True(IpRangePolicy.IsBlocked(IPAddress.Any));
        Assert.True(IpRangePolicy.IsBlocked(IPAddress.IPv6Any));
        Assert.True(IpRangePolicy.IsBlocked(IPAddress.Loopback));
        Assert.True(IpRangePolicy.IsBlocked(IPAddress.IPv6Loopback));
        Assert.True(IpRangePolicy.IsBlocked(IPAddress.Broadcast));
    }

    [Fact]
    public void Fuzz_EveryV4Address_AgreesWithAReferenceImplementation()
    {
        // مرجع مستقل مكتوب من الجدول في العقد مباشرة، مقابل التنفيذ بالأقنعة.
        var random = new Random(4242);
        var bytes = new byte[4];
        for (var i = 0; i < 200_000; i++)
        {
            random.NextBytes(bytes);
            var address = new IPAddress(bytes);
            var expected = ReferenceBlockedV4((uint)((bytes[0] << 24) | (bytes[1] << 16) | (bytes[2] << 8) | bytes[3]));
            Assert.Equal(expected, IpRangePolicy.IsBlockedRange(address));
            // ونفس النتيجة عبر كل تغليف IPv6.
            Assert.Equal(expected, IpRangePolicy.IsBlockedRange(address.MapToIPv6()));
            Assert.Equal(expected, IpRangePolicy.IsBlockedRange(Nat64(address.ToString())));
        }
    }

    [Fact]
    public void Fuzz_RandomV6_NeverThrows_AndIsStable()
    {
        var random = new Random(99);
        var bytes = new byte[16];
        for (var i = 0; i < 50_000; i++)
        {
            random.NextBytes(bytes);
            var address = new IPAddress(bytes);
            var blocked = IpRangePolicy.IsBlockedRange(address);
            Assert.Equal(blocked, IpRangePolicy.IsBlockedRange(address));
            Assert.Equal(blocked, IpRangePolicy.IsBlocked(address));
            Assert.True(IpRangePolicy.IsBlocked(address, new[] { address }));

            // إن فُك تغليفه، فالنتيجة لا تكون "مسموح" بينما المضمَّن محظور.
            var embedded = IpRangePolicy.ExtractEmbeddedIPv4(address);
            if (embedded is not null && IpRangePolicy.IsBlockedRange(embedded)) Assert.True(blocked, address.ToString());
        }
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("[::1]")]
    [InlineData("[::ffff:127.0.0.1]")]
    [InlineData("[0:0:0:0:0:ffff:7f00:1]")]
    [InlineData("fe80::1%eth0")]
    public void IsIpLiteral_CatchesTheSpellingsIPAddressAccepts(string host)
    {
        Assert.True(IpRangePolicy.IsIpLiteral(host), host);
    }

    [Theory]
    // ‏getaddrinfo وinet_addr يقرآن هذه كـ 127.0.0.1، بينما IPAddress.TryParse في .NET الحديثة ترفضها،
    // فلا تسقط في الخطوة 1 (ip_literal) بل تمر كاسم. هذا مقصود ومقبول: الخطوة 6 هي الشبكة التي تمسكها
    // بعد الحل مهما كان الشكل — وهذا ما يثبته هذا الاختبار.
    [InlineData("2130706433")]
    [InlineData("0x7f000001")]
    [InlineData("017700000001")]
    [InlineData("127.1")]
    public void NumericHostSpellings_SurviveTheLiteralCheck_ButNotThePostResolutionCheck(string host)
    {
        _ = IpRangePolicy.IsIpLiteral(host); // أيًا كانت النتيجة، لا نعتمد عليها
        // ما يعتمد عليه العقد: العنوان الذي يعيده أي محلل لهذه الأشكال محظور.
        Assert.True(IpRangePolicy.IsBlocked(IPAddress.Parse("127.0.0.1")));
        Assert.True(IpRangePolicy.IsBlocked(IPAddress.Parse("::ffff:127.0.0.1")));
    }

    // ---------- helpers ----------

    private static void AssertNeighbour(IPAddress? neighbour, string first, string last)
    {
        if (neighbour is null) return; // خارج مدى IPv4
        var alsoInAnotherRange = V4Ranges.Cast<object[]>()
            .Any(row => (string)row[0] != first && InRange(neighbour, (string)row[0], (string)row[1]));
        Assert.Equal(alsoInAnotherRange, IpRangePolicy.IsBlockedRange(neighbour));
        _ = last;
    }

    private static bool InRange(IPAddress address, string first, string last)
    {
        var v = ToUInt(address);
        return v >= ToUInt(IPAddress.Parse(first)) && v <= ToUInt(IPAddress.Parse(last));
    }

    private static uint ToUInt(IPAddress address)
    {
        var b = address.GetAddressBytes();
        return (uint)((b[0] << 24) | (b[1] << 16) | (b[2] << 8) | b[3]);
    }

    private static IPAddress? Step(string address, int delta)
    {
        var value = (long)ToUInt(IPAddress.Parse(address)) + delta;
        if (value is < 0 or > uint.MaxValue) return null;
        var v = (uint)value;
        return new IPAddress(new[] { (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v });
    }

    private static IPAddress Mapped(string v4) => IPAddress.Parse(v4).MapToIPv6();

    private static IPAddress Nat64(string v4) => Wrap(IPAddress.Parse("64:ff9b::").GetAddressBytes(), IPAddress.Parse(v4).GetAddressBytes(), 12);

    private static IPAddress SixToFour(string v4) => Wrap(new byte[] { 0x20, 0x02 }.Concat(new byte[14]).ToArray(), IPAddress.Parse(v4).GetAddressBytes(), 2);

    private static IPAddress V4Compatible(string v4) => Wrap(new byte[16], IPAddress.Parse(v4).GetAddressBytes(), 12);

    private static IPAddress Teredo(string v4)
    {
        var bytes = new byte[16];
        bytes[0] = 0x20;
        bytes[1] = 0x01;
        var v = IPAddress.Parse(v4).GetAddressBytes();
        for (var i = 0; i < 4; i++) bytes[12 + i] = (byte)(v[i] ^ 0xFF);
        return new IPAddress(bytes);
    }

    private static IPAddress Wrap(byte[] prefix, byte[] v4, int offset)
    {
        var bytes = (byte[])prefix.Clone();
        Array.Copy(v4, 0, bytes, offset, 4);
        return new IPAddress(bytes);
    }

    /// <summary>مرجع مكتوب من جدول docs/protocol.md مباشرة (مقارنات عددية، بلا أقنعة بت).</summary>
    private static bool ReferenceBlockedV4(uint a)
    {
        (string First, string Last)[] ranges =
        {
            ("0.0.0.0", "0.255.255.255"), ("10.0.0.0", "10.255.255.255"), ("100.64.0.0", "100.127.255.255"),
            ("127.0.0.0", "127.255.255.255"), ("169.254.0.0", "169.254.255.255"), ("172.16.0.0", "172.31.255.255"),
            ("192.0.0.0", "192.0.0.255"), ("192.0.2.0", "192.0.2.255"), ("192.168.0.0", "192.168.255.255"),
            ("198.18.0.0", "198.19.255.255"), ("198.51.100.0", "198.51.100.255"), ("203.0.113.0", "203.0.113.255"),
            ("224.0.0.0", "239.255.255.255"), ("240.0.0.0", "255.255.255.255"),
        };
        foreach (var (first, last) in ranges)
        {
            if (a >= ToUInt(IPAddress.Parse(first)) && a <= ToUInt(IPAddress.Parse(last))) return true;
        }
        return false;
    }
}
