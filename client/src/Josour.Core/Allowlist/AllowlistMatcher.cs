using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace Josour.Core.Allowlist;

/// <summary>The outcome of evaluating the list; it maps to the OPEN_FAIL reasons in docs/protocol.md section 6.</summary>
public enum AllowlistDecision { Allowed, HostNotAllowed, PortNotAllowed }

/// <summary>
/// The site-list matcher (a pure function shared by both sides) per docs/protocol.md section 6 rule 3.
/// The forms: example.com (a suffix on label boundaries) | =exact.com (exact only) | host:port | =host:port.
/// </summary>
public sealed class AllowlistMatcher : IAllowlist
{
    private static readonly IdnMapping Idn = new();

    public int Version { get; }
    public IReadOnlyList<AllowlistEntry> Entries { get; }

    public AllowlistMatcher(int version, IReadOnlyList<AllowlistEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        Version = version;
        Entries = entries;
    }

    /// <summary>Parses the raw entries. It throws FormatException on the first invalid entry (refusal at load time).</summary>
    public static AllowlistMatcher Parse(int version, IEnumerable<string> rawEntries)
    {
        ArgumentNullException.ThrowIfNull(rawEntries);
        var list = new List<AllowlistEntry>();
        foreach (var raw in rawEntries) list.Add(ParseEntry(raw));
        return new AllowlistMatcher(version, list);
    }

    public static AllowlistEntry ParseEntry(string raw)
        => TryParseEntry(raw, out var entry, out var error) ? entry : throw new FormatException(error);

    /// <summary>
    /// Refused: empty, containing spaces or '*' or '/', a port outside 1..65535, more than one ':', an empty host, or invalid IDN.
    /// </summary>
    public static bool TryParseEntry(string? raw, [NotNullWhen(true)] out AllowlistEntry? entry, [NotNullWhen(false)] out string? error)
    {
        entry = null;
        error = null;
        if (raw is null) { error = "entry is null"; return false; }
        var s = raw;
        if (s.Length == 0) { error = "entry is empty"; return false; }
        if (s.Any(char.IsWhiteSpace)) { error = "entry contains whitespace"; return false; }
        if (s.Contains('/')) { error = "entry contains '/'"; return false; }
        if (s.Contains('*')) { error = "entry contains '*'"; return false; }

        var exact = false;
        if (s[0] == '=') { exact = true; s = s[1..]; }

        int? port = null;
        var colon = s.IndexOf(':');
        if (colon >= 0)
        {
            if (s.IndexOf(':', colon + 1) >= 0) { error = "entry contains more than one ':'"; return false; }
            var portText = s[(colon + 1)..];
            if (!int.TryParse(portText, NumberStyles.None, CultureInfo.InvariantCulture, out var p) || p is < 1 or > 65535)
            {
                error = $"invalid port '{portText}'";
                return false;
            }
            port = p;
            s = s[..colon];
        }

        if (s.Length == 0) { error = "host is empty"; return false; }

        string host;
        try { host = NormalizeHost(s); }
        catch (ArgumentException e) { error = $"invalid host '{s}': {e.Message}"; return false; }

        entry = new AllowlistEntry(host, exact, port);
        return true;
    }

    /// <summary>The maximum name length after Punycode (RFC 1035).</summary>
    public const int MaxHostLength = 253;

    /// <summary>The maximum length of one label after Punycode.</summary>
    public const int MaxLabelLength = 63;

    /// <summary>
    /// Normalising the name: lowercase, strip the trailing dot, IDN to Punycode, then a strict LDH check on the result.
    /// It throws ArgumentException for an invalid name, and every consumer (EgressPolicy and ProxyRouter) turns that into a refusal.
    ///
    /// <para>
    /// Why the strict check after Punycode: our <see cref="IdnMapping"/> has no STD3, so it passes through characters that
    /// are not permitted in a DNS name (NUL, ':', '/', spaces, a leading hyphen in a label). Such a name matches no entry
    /// so it looks safe, but it reaches <c>Dns.GetHostAddressesAsync</c> and the Host header, where one layer reads it
    /// differently from another. Failing closed is cheaper.
    /// </para>
    /// </summary>
    public static string NormalizeHost(string host)
    {
        ArgumentNullException.ThrowIfNull(host);
        var h = host.Trim().ToLowerInvariant();
        foreach (var c in h)
        {
            if (c < 0x20 || c == 0x7F) throw new ArgumentException("host contains a control character", nameof(host));
        }

        while (h.EndsWith('.')) h = h[..^1];
        if (h.Length == 0) throw new ArgumentException("host is empty", nameof(host));

        string ascii;
        try
        {
            ascii = Idn.GetAscii(h).ToLowerInvariant();
        }
        catch (ArgumentException e)
        {
            throw new ArgumentException("host is not a valid IDN/DNS name", nameof(host), e);
        }

        // GetAscii converts the IDN separators (U+3002 and its siblings) into '.', so a trailing dot may appear that was not in the original.
        while (ascii.EndsWith('.')) ascii = ascii[..^1];
        ValidateAsciiHost(ascii);
        return ascii;
    }

    /// <summary>The name after Punycode: a total length of ≤ 253, and every label 1..63 of [a-z0-9-_] with no hyphen at either end.</summary>
    private static void ValidateAsciiHost(string ascii)
    {
        if (ascii.Length == 0) throw new ArgumentException("host is empty", nameof(ascii));
        if (ascii.Length > MaxHostLength) throw new ArgumentException($"host is longer than {MaxHostLength} characters", nameof(ascii));

        var start = 0;
        while (true)
        {
            var dot = ascii.IndexOf('.', start);
            var end = dot < 0 ? ascii.Length : dot;
            var length = end - start;
            if (length == 0) throw new ArgumentException("host has an empty label", nameof(ascii));
            if (length > MaxLabelLength) throw new ArgumentException($"host has a label longer than {MaxLabelLength} characters", nameof(ascii));
            if (ascii[start] == '-' || ascii[end - 1] == '-') throw new ArgumentException("host label starts or ends with '-'", nameof(ascii));
            for (var i = start; i < end; i++)
            {
                var c = ascii[i];
                var allowed = c is >= 'a' and <= 'z' || c is >= '0' and <= '9' || c == '-' || c == '_';
                if (!allowed) throw new ArgumentException($"host label contains '{c}'", nameof(ascii));
            }

            if (dot < 0) return;
            start = dot + 1;
        }
    }

    public bool HostMatches(string normalizedHost)
        => Entries.Any(entry => HostMatches(normalizedHost, entry));

    public bool IsAllowed(string normalizedHost, int port, IReadOnlyList<int> allowedPorts)
        => Evaluate(normalizedHost, port, allowedPorts) == AllowlistDecision.Allowed;

    /// <summary>
    /// It distinguishes the name not matching (not_allowed) from the port not being permitted (port_not_allowed).
    /// An entry carrying a port matches that port only; without one, the allowedPorts are permitted.
    /// </summary>
    public AllowlistDecision Evaluate(string normalizedHost, int port, IReadOnlyList<int> allowedPorts)
    {
        ArgumentNullException.ThrowIfNull(normalizedHost);
        ArgumentNullException.ThrowIfNull(allowedPorts);
        var hostMatched = false;
        foreach (var entry in Entries)
        {
            if (!HostMatches(normalizedHost, entry)) continue;
            hostMatched = true;
            if (entry.Port is int entryPort)
            {
                if (entryPort == port) return AllowlistDecision.Allowed;
            }
            else if (allowedPorts.Contains(port))
            {
                return AllowlistDecision.Allowed;
            }
        }
        return hostMatched ? AllowlistDecision.PortNotAllowed : AllowlistDecision.HostNotAllowed;
    }

    /// <summary>
    /// An exact match, or a suffix on label boundaries (a.example.com matches example.com; notexample.com does not).
    /// A subdomain must carry at least one label, so ".example.com" (an unnormalised name) does not match the entry "example.com".
    /// </summary>
    public static bool HostMatches(string normalizedHost, AllowlistEntry entry)
    {
        ArgumentNullException.ThrowIfNull(normalizedHost);
        ArgumentNullException.ThrowIfNull(entry);
        if (string.Equals(normalizedHost, entry.Host, StringComparison.Ordinal)) return true;
        if (entry.ExactOnly) return false;
        return normalizedHost.Length > entry.Host.Length + 1
            && normalizedHost.EndsWith(entry.Host, StringComparison.Ordinal)
            && normalizedHost[normalizedHost.Length - entry.Host.Length - 1] == '.';
    }
}
