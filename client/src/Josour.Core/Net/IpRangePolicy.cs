using System.Net;
using System.Net.Sockets;

namespace Josour.Core.Net;

/// <summary>
/// العناوين المحظورة للخروج من المضيف (docs/protocol.md القسم 6 القاعدة 6). دالة نقية بلا I/O.
/// عناوين IPv6 المغلِّفة لـ IPv4 (::ffff:0:0/96، 64:ff9b::/96، 2002::/16، 2001::/32 Teredo) تُفك ويُفحص العنوان المضمَّن.
/// </summary>
public static class IpRangePolicy
{
    private static readonly (byte[] Prefix, int Bits)[] BlockedV4 =
    {
        P("0.0.0.0", 8), P("10.0.0.0", 8), P("100.64.0.0", 10), P("127.0.0.0", 8), P("169.254.0.0", 16),
        P("172.16.0.0", 12), P("192.0.0.0", 24), P("192.0.2.0", 24), P("192.168.0.0", 16), P("198.18.0.0", 15),
        P("198.51.100.0", 24), P("203.0.113.0", 24), P("224.0.0.0", 4), P("240.0.0.0", 4), P("255.255.255.255", 32),
    };

    private static readonly (byte[] Prefix, int Bits)[] BlockedV6 =
    {
        P("::", 128), P("::1", 128), P("fc00::", 7), P("fe80::", 10), P("ff00::", 8),
    };

    private static readonly byte[] Nat64Prefix = IPAddress.Parse("64:ff9b::").GetAddressBytes();
    private static readonly byte[] IPv4CompatiblePrefix = IPAddress.Parse("::").GetAddressBytes();

    private static (byte[] Prefix, int Bits) P(string address, int bits) => (IPAddress.Parse(address).GetAddressBytes(), bits);

    /// <summary>هل العنوان ضمن النطاقات المحظورة (بعد فك التغليف) أو يساوي أحد عناوين المضيف نفسه؟</summary>
    public static bool IsBlocked(IPAddress address, IEnumerable<IPAddress>? localAddresses = null)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (IsBlockedRange(address)) return true;
        if (localAddresses is null) return false;

        var embedded = ExtractEmbeddedIPv4(address);
        foreach (var local in localAddresses)
        {
            if (local is null) continue;
            var localEmbedded = ExtractEmbeddedIPv4(local);
            if (SameAddress(address, local) || SameAddress(embedded, local) || SameAddress(address, localEmbedded) || SameAddress(embedded, localEmbedded))
                return true;
        }
        return false;
    }

    /// <summary>فحص النطاقات فقط (بلا عناوين المضيف).</summary>
    public static bool IsBlockedRange(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        switch (address.AddressFamily)
        {
            case AddressFamily.InterNetwork:
            {
                Span<byte> bytes = stackalloc byte[4];
                if (!address.TryWriteBytes(bytes, out _)) return true;
                foreach (var (prefix, bits) in BlockedV4)
                    if (Matches(bytes, prefix, bits)) return true;
                return false;
            }
            case AddressFamily.InterNetworkV6:
            {
                Span<byte> bytes = stackalloc byte[16];
                if (!address.TryWriteBytes(bytes, out _)) return true;
                foreach (var (prefix, bits) in BlockedV6)
                    if (Matches(bytes, prefix, bits)) return true;
                var embedded = ExtractEmbeddedIPv4(address);
                return embedded is not null && IsBlockedRange(embedded);
            }
            default:
                return true; // عائلات أخرى لا تُطلب أبدًا
        }
    }

    /// <summary>
    /// يعيد IPv4 المضمَّن في عنوان IPv6 مغلِّف، أو null:
    /// ::ffff:a.b.c.d (آخر 32 بت)، ::a.b.c.d المهجور (RFC 4291 §2.5.5.1، آخر 32 بت)، 64:ff9b::/96 (آخر 32 بت)،
    /// 2002::/16 (البتات 16-47)، 2001::/32 Teredo (آخر 32 بت XOR 0xFFFFFFFF).
    /// <para>
    /// <c>::</c> و<c>::1</c> مستثنيان من فك <c>::/96</c> لأنهما محظوران أصلًا بقاعدتيهما الصريحتين، ولأن فكّهما
    /// إلى <c>0.0.0.0</c> و<c>0.0.0.1</c> يجعلهما يطابقان عناوين محلية بلا معنى.
    /// </para>
    /// </summary>
    public static IPAddress? ExtractEmbeddedIPv4(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (address.AddressFamily != AddressFamily.InterNetworkV6) return null;
        Span<byte> b = stackalloc byte[16];
        if (!address.TryWriteBytes(b, out _)) return null;

        if (address.IsIPv4MappedToIPv6) return new IPAddress(b[12..16]);
        if (Matches(b, Nat64Prefix, 96)) return new IPAddress(b[12..16]);
        // ::a.b.c.d المهجور: مهجور لا يعني غير قابل للطلب — محلل DNS خبيث قد يعيده ليتجاوز فحص v4.
        if (Matches(b, IPv4CompatiblePrefix, 96) && !(b[12] == 0 && b[13] == 0 && b[14] == 0 && b[15] <= 1))
            return new IPAddress(b[12..16]);
        if (b[0] == 0x20 && b[1] == 0x02) return new IPAddress(b[2..6]);
        if (b[0] == 0x20 && b[1] == 0x01 && b[2] == 0x00 && b[3] == 0x00)
        {
            Span<byte> v4 = stackalloc byte[4];
            for (var i = 0; i < 4; i++) v4[i] = (byte)(b[12 + i] ^ 0xFF);
            return new IPAddress(v4);
        }
        return null;
    }

    /// <summary>هل الاسم عنوان IP حرفي (v4 أو v6، ولو بين أقواس مربعة)؟</summary>
    public static bool IsIpLiteral(string? host)
    {
        if (string.IsNullOrWhiteSpace(host)) return false;
        var h = host.Trim();
        if (h.Length >= 2 && h[0] == '[' && h[^1] == ']') h = h[1..^1];
        return IPAddress.TryParse(h, out _);
    }

    private static bool SameAddress(IPAddress? a, IPAddress? b)
    {
        if (a is null || b is null) return false;
        if (a.AddressFamily != b.AddressFamily) return false;
        Span<byte> ab = stackalloc byte[16];
        Span<byte> bb = stackalloc byte[16];
        if (!a.TryWriteBytes(ab, out var al) || !b.TryWriteBytes(bb, out var bl)) return false;
        return al == bl && ab[..al].SequenceEqual(bb[..bl]);
    }

    private static bool Matches(ReadOnlySpan<byte> address, ReadOnlySpan<byte> prefix, int bits)
    {
        if (address.Length != prefix.Length) return false;
        var full = bits / 8;
        var rem = bits % 8;
        if (!address[..full].SequenceEqual(prefix[..full])) return false;
        if (rem == 0) return true;
        var mask = (0xFF << (8 - rem)) & 0xFF;
        return (address[full] & mask) == (prefix[full] & mask);
    }
}
