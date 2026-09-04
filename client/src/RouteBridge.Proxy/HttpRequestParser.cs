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
/// </summary>
public static class HttpRequestParser
{
    public const int MaxHeadBytes = 16 * 1024;
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
        if (method.Length == 0 || method.Any(c => c <= ' ' || c > '~')) throw new HttpParseException("malformed method");

        var headers = new List<KeyValuePair<string, string>>();
        for (var i = 1; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.Length == 0) break;
            var colon = line.IndexOf(':');
            if (colon <= 0) throw new HttpParseException("malformed header line");
            var name = line[..colon].Trim();
            var value = line[(colon + 1)..].Trim();
            if (name.Length == 0 || name.Any(c => c <= ' ' || c > '~')) throw new HttpParseException("malformed header name");
            headers.Add(new KeyValuePair<string, string>(name, value));
        }
        return new ProxyRequest(method, target, version, headers, remainder);
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
