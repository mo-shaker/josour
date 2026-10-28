using System.Net;
using System.Net.Sockets;

namespace Josour.Core.Net;

/// <summary>
/// The addresses blocked for egress from the host (docs/protocol.md section 6 rule 6). A pure function with no I/O.
/// IPv6 addresses that wrap IPv4 (::ffff:0:0/96, ::ffff:0:0:0/96 SIIT, 64:ff9b::/96, 64:ff9b:1::/48, 2002::/16, 2001::/32 Teredo)
/// are unwrapped and the embedded address is checked.
/// </summary>
public static class IpRangePolicy
{
    private static readonly (byte[] Prefix, int Bits)[] BlockedV4 =
    {
        P("0.0.0.0", 8), P("10.0.0.0", 8), P("100.64.0.0", 10), P("127.0.0.0", 8), P("169.254.0.0", 16),
        P("172.16.0.0", 12), P("192.0.0.0", 24), P("192.0.2.0", 24), P("192.88.99.0", 24), P("192.168.0.0", 16),
        P("198.18.0.0", 15), P("198.51.100.0", 24), P("203.0.113.0", 24), P("224.0.0.0", 4), P("240.0.0.0", 4),
        P("255.255.255.255", 32),
    };

    private static readonly (byte[] Prefix, int Bits)[] BlockedV6 =
    {
        P("::", 128), P("::1", 128), P("100::", 64), P("2001:db8::", 32), P("fc00::", 7), P("fe80::", 10), P("fec0::", 10),
        P("ff00::", 8),
    };

    private static readonly byte[] Nat64Prefix = IPAddress.Parse("64:ff9b::").GetAddressBytes();
    private static readonly byte[] Nat64LocalUsePrefix = IPAddress.Parse("64:ff9b:1::").GetAddressBytes();
    private static readonly byte[] SiitPrefix = IPAddress.Parse("::ffff:0:0:0").GetAddressBytes();
    private static readonly byte[] IPv4CompatiblePrefix = IPAddress.Parse("::").GetAddressBytes();

    private static (byte[] Prefix, int Bits) P(string address, int bits) => (IPAddress.Parse(address).GetAddressBytes(), bits);

    /// <summary>Is the address inside the blocked ranges (after unwrapping), or equal to one of the host's own addresses?</summary>
    public static bool IsBlocked(IPAddress address, IEnumerable<IPAddress>? localAddresses = null)
        => IsBlocked(address, localAddresses, null);

    /// <summary>
    /// Is the address inside the blocked ranges (after unwrapping), equal to one of the host's own addresses, or inside one of the
    /// host's on-link networks (<paramref name="localNetworks"/>, docs/protocol.md section 6 rule 6)? Every IPv4 an IPv6 address may
    /// carry is checked against the networks too.
    /// </summary>
    public static bool IsBlocked(IPAddress address, IEnumerable<IPAddress>? localAddresses, IEnumerable<IPNetwork>? localNetworks)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (IsBlockedRange(address)) return true;
        if (localAddresses is null && localNetworks is null) return false;

        var embedded = EmbeddedIPv4Candidates(address);
        if (localAddresses is not null)
        {
            foreach (var local in localAddresses)
            {
                if (local is null) continue;
                if (SameAddress(address, local)) return true;
                var localEmbedded = EmbeddedIPv4Candidates(local);
                foreach (var e in embedded)
                    if (SameAddress(e, local) || localEmbedded.Any(le => SameAddress(e, le))) return true;
                if (localEmbedded.Any(le => SameAddress(address, le))) return true;
            }
        }

        if (localNetworks is not null)
        {
            foreach (var network in localNetworks)
            {
                if (network.Contains(address)) return true;
                foreach (var e in embedded)
                    if (network.Contains(e)) return true;
            }
        }
        return false;
    }

    /// <summary>The ranges only (without the host's own addresses).</summary>
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
                foreach (var embedded in EmbeddedIPv4Candidates(address))
                    if (IsBlockedRange(embedded)) return true;
                return false;
            }
            default:
                return true; // other families are never requested
        }
    }

    /// <summary>
    /// Returns the IPv4 embedded in a wrapping IPv6 address, or null:
    /// ::ffff:a.b.c.d (the last 32 bits), ::ffff:0:a.b.c.d SIIT (the last 32 bits), the deprecated ::a.b.c.d (RFC 4291 §2.5.5.1, the last 32 bits),
    /// 64:ff9b::/96 (the last 32 bits), 64:ff9b:1::/48 (the last 32 bits; see <see cref="EmbeddedIPv4Candidates"/> for the other RFC 6052 positions),
    /// 2002::/16 (bits 16-47), 2001::/32 Teredo (the last 32 bits XOR 0xFFFFFFFF).
    /// <para>
    /// <c>::</c> and <c>::1</c> are excluded from the <c>::/96</c> unwrapping because they are already blocked by their own explicit
    /// rules, and because unwrapping them to <c>0.0.0.0</c> and <c>0.0.0.1</c> would make them match local addresses meaninglessly.
    /// </para>
    /// </summary>
    public static IPAddress? ExtractEmbeddedIPv4(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        var all = EmbeddedIPv4Candidates(address);
        return all.Count == 0 ? null : all[0];
    }

    /// <summary>
    /// Every IPv4 address the IPv6 address may carry, the most likely first. Only the RFC 8215 local-use NAT64 prefix
    /// (64:ff9b:1::/48) has more than one: the operator picks the translator's prefix length inside the /48, so the RFC 6052
    /// positions for /96, /64, /56 and /48 are returned (each one only where the encoding is valid for it), and the address is
    /// blocked if any of them is.
    /// </summary>
    public static IReadOnlyList<IPAddress> EmbeddedIPv4Candidates(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (address.AddressFamily != AddressFamily.InterNetworkV6) return Array.Empty<IPAddress>();
        Span<byte> b = stackalloc byte[16];
        if (!address.TryWriteBytes(b, out _)) return Array.Empty<IPAddress>();

        if (address.IsIPv4MappedToIPv6) return new[] { new IPAddress(b[12..16]) };
        if (Matches(b, SiitPrefix, 96)) return new[] { new IPAddress(b[12..16]) };
        if (Matches(b, Nat64Prefix, 96)) return new[] { new IPAddress(b[12..16]) };
        if (Matches(b, Nat64LocalUsePrefix, 48))
        {
            // RFC 6052 §2.2: the operator picks the translator's prefix length inside the /48, so each position is a candidate, but only
            // where the address is a valid encoding for it — the u-octet (byte 8) and the suffix after the IPv4 are zero. Otherwise an
            // ordinary /96 translation (64:ff9b:1::8.8.8.8) would read as 0.0.0.8 at /64 and every IPv4 site behind the translator would be refused.
            // A reading in 0.0.0.0/8 is dropped: no translator forwards to "this network", and it is exactly what the zero suffix of a
            // /48 or /56 encoding reads as at /96, which would otherwise refuse every site behind such a translator.
            var candidates = new List<IPAddress>(4);
            AddUnlessThisNetwork(candidates, b[12], b[13], b[14], b[15]);                                      // /96
            if (b[8] == 0)
            {
                if (IsZero(b[13..16])) AddUnlessThisNetwork(candidates, b[9], b[10], b[11], b[12]);            // /64
                if (IsZero(b[12..16])) AddUnlessThisNetwork(candidates, b[7], b[9], b[10], b[11]);             // /56
                if (IsZero(b[11..16])) AddUnlessThisNetwork(candidates, b[6], b[7], b[9], b[10]);              // /48
            }
            return candidates;
        }
        // The deprecated ::a.b.c.d: deprecated does not mean unrequestable — a malicious DNS resolver could return it to get past the v4 check.
        if (Matches(b, IPv4CompatiblePrefix, 96) && !(b[12] == 0 && b[13] == 0 && b[14] == 0 && b[15] <= 1))
            return new[] { new IPAddress(b[12..16]) };
        if (b[0] == 0x20 && b[1] == 0x02) return new[] { new IPAddress(b[2..6]) };
        if (b[0] == 0x20 && b[1] == 0x01 && b[2] == 0x00 && b[3] == 0x00)
        {
            var v4 = new byte[4];
            for (var i = 0; i < 4; i++) v4[i] = (byte)(b[12 + i] ^ 0xFF);
            return new[] { new IPAddress(v4) };
        }
        return Array.Empty<IPAddress>();
    }

    /// <summary>Is the name an IP address literal (v4 or v6, even in square brackets)?</summary>
    public static bool IsIpLiteral(string? host)
    {
        if (string.IsNullOrWhiteSpace(host)) return false;
        var h = host.Trim();
        if (h.Length >= 2 && h[0] == '[' && h[^1] == ']') h = h[1..^1];
        return IPAddress.TryParse(h, out _);
    }

    private static void AddUnlessThisNetwork(List<IPAddress> list, byte a, byte b, byte c, byte d)
    {
        if (a != 0) list.Add(new IPAddress(new[] { a, b, c, d }));
    }

    private static bool IsZero(ReadOnlySpan<byte> bytes) => bytes.IndexOfAnyExcept((byte)0) < 0;

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
