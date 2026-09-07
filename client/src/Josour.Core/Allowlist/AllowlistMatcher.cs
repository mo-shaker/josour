using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace Josour.Core.Allowlist;

/// <summary>نتيجة تقييم القائمة؛ تُربط بأسباب OPEN_FAIL في docs/protocol.md القسم 6.</summary>
public enum AllowlistDecision { Allowed, HostNotAllowed, PortNotAllowed }

/// <summary>
/// مطابق قائمة المواقع (دالة نقية مشتركة بين الطرفين) حسب docs/protocol.md القسم 6 القاعدة 3.
/// الصيغ: example.com (لاحقة على حدود التسميات) | =exact.com (تامة فقط) | host:port | =host:port.
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

    /// <summary>يحلل المدخلات الخام. يرمي FormatException عند أول مدخل غير صالح (الرفض عند التحميل).</summary>
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
    /// مرفوض: فارغ، يحتوي مسافات أو '*' أو '/'، منفذ خارج 1..65535، أكثر من ':' واحدة، مضيف فارغ، أو IDN غير صالح.
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

    /// <summary>أقصى طول اسم بعد Punycode (RFC 1035).</summary>
    public const int MaxHostLength = 253;

    /// <summary>أقصى طول تسمية واحدة بعد Punycode.</summary>
    public const int MaxLabelLength = 63;

    /// <summary>
    /// تطبيع الاسم: أحرف صغيرة، حذف النقطة الأخيرة، IDN إلى Punycode، ثم فحص LDH صارم على الناتج.
    /// يرمي ArgumentException للاسم غير الصالح، وكل المستهلكين (EgressPolicy وProxyRouter) يترجمون ذلك إلى رفض.
    ///
    /// <para>
    /// لماذا الفحص الصارم بعد Punycode: <see cref="IdnMapping"/> عندنا بلا STD3، فيمرر محارف لا تجوز في اسم DNS
    /// (NUL، ':' ، '/'، مسافات، شرطة في أول التسمية). اسم كهذا لا يطابق أي مدخل فيبدو آمنًا، لكنه يصل إلى
    /// <c>Dns.GetHostAddressesAsync</c> وإلى ترويسة Host، حيث تختلف قراءته بين طبقة وأخرى. الفشل المغلق أرخص.
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

        // GetAscii يحوّل فواصل IDN (U+3002 وأخواتها) إلى '.'، فقد تظهر نقطة أخيرة لم تكن في الأصل.
        while (ascii.EndsWith('.')) ascii = ascii[..^1];
        ValidateAsciiHost(ascii);
        return ascii;
    }

    /// <summary>الاسم بعد Punycode: طول كلي ≤ 253، كل تسمية 1..63 من [a-z0-9-_] بلا شرطة في طرفيها.</summary>
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

    public bool IsAllowed(string normalizedHost, int port, IReadOnlyList<int> allowedPorts)
        => Evaluate(normalizedHost, port, allowedPorts) == AllowlistDecision.Allowed;

    /// <summary>
    /// يميّز بين عدم مطابقة الاسم (not_allowed) وعدم السماح بالمنفذ (port_not_allowed).
    /// المدخل الذي يحمل منفذًا يطابق ذلك المنفذ فقط؛ بدونه يُسمح بمنافذ allowedPorts.
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
    /// مطابقة تامة، أو لاحقة على حدود التسميات (a.example.com يطابق example.com؛ notexample.com لا يطابق).
    /// النطاق الفرعي يجب أن يحمل تسمية واحدة على الأقل، فلا يطابق ".example.com" (اسم غير مطبَّع) مدخل "example.com".
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
