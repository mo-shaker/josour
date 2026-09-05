using System.Globalization;
using System.Text;

namespace RouteBridge.Proxy;

public sealed class HttpParseException : Exception
{
    public HttpParseException(string message) : base(message) { }
}

/// <summary>رأس طلب HTTP كما وصل من المتصفح، مع أي بايتات تلته في نفس القراءة (بداية الجسم أو TLS بعد CONNECT).</summary>
public sealed class ProxyRequest
{
    public ProxyRequest(string method, string target, string version, IReadOnlyList<KeyValuePair<string, string>> headers, byte[] remainder)
    {
        Method = method;
        Target = target;
        Version = version;
        Headers = headers;
        Remainder = remainder;
    }

    public string Method { get; }
    public string Target { get; }
    public string Version { get; }
    public IReadOnlyList<KeyValuePair<string, string>> Headers { get; }
    public byte[] Remainder { get; }
    public bool IsConnect => string.Equals(Method, "CONNECT", StringComparison.OrdinalIgnoreCase);

    public string? Header(string name)
        => Headers.FirstOrDefault(h => string.Equals(h.Key, name, StringComparison.OrdinalIgnoreCase)).Value;
}

/// <summary>
/// محلل رأس HTTP/1.x بلا تخصيص زائد: يقرأ حتى CRLFCRLF (حد 16 KiB)، يفصل سطر الطلب والرؤوس، ويحلل الصيغ التي يرسلها
/// المتصفح إلى Proxy: `CONNECT host:port` (بما فيه `[IPv6]:port`) و`GET http://host/path` (absolute-URI) و origin-form مع Host.
///
/// <para>
/// تشدّد الأسبوع 4 (منع تهريب الطلبات وحقن الرؤوس): كل ما يخرج من هنا يُعاد كتابته حرفيًا نحو الأصل في
/// <c>ConnectProxyServer.BuildOriginFormHead</c>، فأي CR أو LF أو NUL داخل الهدف أو الإصدار أو قيمة رأس يعني سطرًا
/// إضافيًا في طلب الأصل. لذلك يُرفض هنا (400) بدل تمريره:
/// </para>
/// <list type="bullet">
///   <item>سطر الطلب: الهدف والإصدار مرئيان ASCII فقط (0x21..0x7E).</item>
///   <item>اسم الرأس: token بلا مسافة قبل ':' (المسافة قبل النقطتين ناقل تهريب معروف).</item>
///   <item>قيمة الرأس: بلا محارف تحكم (يُسمح HTAB و obs-text ≥ 0x80 كما في RFC 7230).</item>
///   <item>لا obs-fold (سطر يبدأ بمسافة أو HTAB) — RFC 7230 يوصي برفضه في الوسطاء.</item>
///   <item>Host واحد على الأكثر، ولا <c>Content-Length</c> مع <c>Transfer-Encoding</c> معًا (RFC 7230 §3.3.3).</item>
///   <item>عدد الرؤوس ≤ <see cref="MaxHeaders"/>.</item>
/// </list>
/// </summary>
public static class HttpRequestParser
{
    public const int MaxHeadBytes = 16 * 1024;

    /// <summary>أقصى عدد رؤوس في الطلب الواحد؛ المتصفحات لا تقترب منه والحد يمنع رؤوسًا بالآلاف داخل 16 KiB.</summary>
    public const int MaxHeaders = 200;

    private static ReadOnlySpan<byte> HeadEnd => "\r\n\r\n"u8;

    /// <summary>null عند إغلاق الاتصال قبل أي بايت؛ HttpParseException عند رأس تالف أو أكبر من الحد.</summary>
    public static async Task<ProxyRequest?> ReadAsync(Stream stream, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var buffer = new byte[MaxHeadBytes];
        var filled = 0;
        var scanFrom = 0;
        while (true)
        {
            var idx = filled >= 4 ? buffer.AsSpan(Math.Max(0, scanFrom - 3), filled - Math.Max(0, scanFrom - 3)).IndexOf(HeadEnd) : -1;
            if (idx >= 0)
            {
                var end = Math.Max(0, scanFrom - 3) + idx + 4;
                var remainder = buffer.AsSpan(end, filled - end).ToArray();
                return Parse(buffer.AsSpan(0, end), remainder);
            }
            if (filled == MaxHeadBytes) throw new HttpParseException("request head exceeds 16 KiB");
            var n = await stream.ReadAsync(buffer.AsMemory(filled), ct).ConfigureAwait(false);
            if (n == 0)
            {
                if (filled == 0) return null;
                throw new HttpParseException("connection closed before the request head completed");
            }
            scanFrom = filled;
            filled += n;
        }
    }

    public static ProxyRequest Parse(ReadOnlySpan<byte> head, byte[] remainder)
    {
        var text = Encoding.Latin1.GetString(head);
        var lines = text.Split("\r\n");
        // السطر الأول: METHOD SP target SP HTTP/x.y (نتسامح مع مسافات متعددة)
        var requestLine = lines[0].Trim();
        var parts = requestLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 3) throw new HttpParseException("malformed request line");
        var method = parts[0];
        var target = parts[1];
        var version = parts[2];
        if (!version.StartsWith("HTTP/1.", StringComparison.Ordinal)) throw new HttpParseException("unsupported HTTP version");
        if (method.Length == 0 || !IsVisibleAscii(method)) throw new HttpParseException("malformed method");
        // الهدف والإصدار يُعاد بناؤهما نحو الأصل: أي CR/LF/NUL/بايت غير مرئي هنا = سطر إضافي في طلب الأصل.
        if (target.Length == 0 || !IsVisibleAscii(target)) throw new HttpParseException("malformed request target");
        if (!IsVisibleAscii(version)) throw new HttpParseException("malformed HTTP version");

        var headers = new List<KeyValuePair<string, string>>();
        var hostHeaders = 0;
        var hasContentLength = false;
        var hasTransferEncoding = false;
        for (var i = 1; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.Length == 0) break;
            if (line[0] is ' ' or '\t') throw new HttpParseException("obsolete header line folding is not accepted");
            var colon = line.IndexOf(':');
            if (colon <= 0) throw new HttpParseException("malformed header line");
            var name = line[..colon];
            var value = line[(colon + 1)..].Trim();
            // اسم بلا مسافة إطلاقًا: "Foo : bar" يُقرأ Foo عندنا وقد يُقرأ "Foo " عند الأصل — ناقل تهريب.
            if (name.Length == 0 || !IsVisibleAscii(name)) throw new HttpParseException("malformed header name");
            if (!IsFieldValueSafe(value)) throw new HttpParseException("malformed header value");
            if (headers.Count == MaxHeaders) throw new HttpParseException($"more than {MaxHeaders} headers");

            if (name.Equals("Host", StringComparison.OrdinalIgnoreCase) && ++hostHeaders > 1)
                throw new HttpParseException("duplicated Host header");
            if (name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)) hasContentLength = true;
            if (name.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase)) hasTransferEncoding = true;

            headers.Add(new KeyValuePair<string, string>(name, value));
        }
        // RFC 7230 §3.3.3 القاعدة 3: الرسالة التي تحمل الاثنين غامضة الطول؛ الوسيط يرفضها ولا يمررها.
        if (hasContentLength && hasTransferEncoding) throw new HttpParseException("both Content-Length and Transfer-Encoding");
        return new ProxyRequest(method, target, version, headers, remainder);
    }

    /// <summary>ASCII مرئي فقط (0x21..0x7E): بلا مسافات ولا محارف تحكم ولا بايتات ≥ 0x80.</summary>
    private static bool IsVisibleAscii(string text)
    {
        foreach (var c in text)
        {
            if (c <= ' ' || c > '~') return false;
        }
        return true;
    }

    /// <summary>قيمة رأس آمنة للتمرير: بلا محارف تحكم (يُسمح HTAB) وبلا DEL؛ obs-text (≥ 0x80) مسموح.</summary>
    private static bool IsFieldValueSafe(string value)
    {
        foreach (var c in value)
        {
            if ((c < 0x20 && c != '\t') || c == 0x7F) return false;
        }
        return true;
    }

    /// <summary>host[:port] أو [v6]:port أو [v6]. المضيف يعود بلا أقواس. port خارج 1..65535 = فشل.</summary>
    public static bool TryParseAuthority(string authority, int defaultPort, out string host, out int port)
    {
        host = string.Empty;
        port = defaultPort;
        if (string.IsNullOrWhiteSpace(authority)) return false;
        var a = authority.Trim();
        if (a[0] == '[')
        {
            var close = a.IndexOf(']');
            if (close < 0) return false;
            host = a[1..close];
            if (host.Length == 0) return false;
            var rest = a[(close + 1)..];
            if (rest.Length == 0) return true;
            if (rest[0] != ':') return false;
            return TryParsePort(rest[1..], out port);
        }

        var colon = a.IndexOf(':');
        if (colon < 0)
        {
            host = a;
            return host.Length > 0 && !host.Contains('/');
        }
        if (a.IndexOf(':', colon + 1) >= 0) return false; // IPv6 بلا أقواس أو صيغة تالفة
        host = a[..colon];
        return host.Length > 0 && !host.Contains('/') && TryParsePort(a[(colon + 1)..], out port);
    }

    /// <summary>absolute-URI بمخطط http فقط. المضيف بلا أقواس؛ المسار يبدأ بـ '/'.</summary>
    public static bool TryParseHttpUri(string target, out string host, out int port, out string pathAndQuery)
    {
        host = string.Empty;
        port = 80;
        pathAndQuery = "/";
        if (!Uri.TryCreate(target, UriKind.Absolute, out var uri)) return false;
        if (!string.Equals(uri.Scheme, "http", StringComparison.OrdinalIgnoreCase)) return false;
        if (string.IsNullOrEmpty(uri.Host)) return false;
        host = uri.HostNameType == UriHostNameType.IPv6 ? uri.Host.Trim('[', ']') : uri.Host;
        port = uri.IsDefaultPort ? 80 : uri.Port;
        pathAndQuery = string.IsNullOrEmpty(uri.PathAndQuery) ? "/" : uri.PathAndQuery;
        return port is >= 1 and <= 65535;
    }

    private static bool TryParsePort(string text, out int port)
        => int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out port) && port is >= 1 and <= 65535;
}
