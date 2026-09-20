using System.Globalization;
using System.Text;

namespace Josour.Proxy;

public sealed class HttpParseException : Exception
{
    public HttpParseException(string message) : base(message) { }
}

/// <summary>An HTTP request head as it arrived from the browser, with any bytes that followed it in the same read (the start of the body, or TLS after CONNECT).</summary>
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
/// An HTTP/1.x head parser with no excess allocation: it reads up to CRLFCRLF (a 16 KiB limit), splits the request line from the headers, and parses the forms the
/// browser sends to a proxy: `CONNECT host:port` (including `[IPv6]:port`), `GET http://host/path` (an absolute-URI) and origin-form with a Host.
///
/// <para>
/// Week four's hardening (against request smuggling and header injection): everything that comes out of here is rewritten literally towards the origin in
/// <c>ConnectProxyServer.BuildOriginFormHead</c>, so any CR or LF or NUL inside the target, the version or a header value means an extra line
/// in the origin's request. So it is refused here (400) rather than passed on:
/// </para>
/// <list type="bullet">
///   <item>The request line: the target and the version are visible ASCII only (0x21..0x7E).</item>
///   <item>The header name: a token with no space before ':' (a space before the colon is a known smuggling vector).</item>
///   <item>The header value: no control characters (HTAB and obs-text >= 0x80 are permitted, as in RFC 7230).</item>
///   <item>No obs-fold (a line starting with a space or HTAB) — RFC 7230 recommends intermediaries refuse it.</item>
///   <item>At most one Host, and never <c>Content-Length</c> together with <c>Transfer-Encoding</c> (RFC 7230 §3.3.3).</item>
///   <item>The number of headers <= <see cref="MaxHeaders"/>.</item>
/// </list>
/// </summary>
public static class HttpRequestParser
{
    public const int MaxHeadBytes = 16 * 1024;

    /// <summary>The largest number of headers in one request; browsers come nowhere near it, and the limit stops thousands of headers inside 16 KiB.</summary>
    public const int MaxHeaders = 200;

    private static ReadOnlySpan<byte> HeadEnd => "\r\n\r\n"u8;

    /// <summary>null when the connection closes before any byte; HttpParseException on a malformed head or one larger than the limit.</summary>
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
        // The first line: METHOD SP target SP HTTP/x.y (we tolerate repeated spaces)
        var requestLine = lines[0].Trim();
        var parts = requestLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 3) throw new HttpParseException("malformed request line");
        var method = parts[0];
        var target = parts[1];
        var version = parts[2];
        if (!version.StartsWith("HTTP/1.", StringComparison.Ordinal)) throw new HttpParseException("unsupported HTTP version");
        if (method.Length == 0 || !IsVisibleAscii(method)) throw new HttpParseException("malformed method");
        // The target and the version are rebuilt towards the origin: any CR/LF/NUL/non-printable byte here = an extra line in the origin's request.
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
            // A name with no space at all: "Foo : bar" reads as Foo here and may read as "Foo " at the origin — a smuggling vector.
            if (name.Length == 0 || !IsVisibleAscii(name)) throw new HttpParseException("malformed header name");
            if (!IsFieldValueSafe(value)) throw new HttpParseException("malformed header value");
            if (headers.Count == MaxHeaders) throw new HttpParseException($"more than {MaxHeaders} headers");

            if (name.Equals("Host", StringComparison.OrdinalIgnoreCase) && ++hostHeaders > 1)
                throw new HttpParseException("duplicated Host header");
            if (name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)) hasContentLength = true;
            if (name.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase)) hasTransferEncoding = true;

            headers.Add(new KeyValuePair<string, string>(name, value));
        }
        // RFC 7230 §3.3.3 rule 3: a message carrying both has an ambiguous length; an intermediary refuses it rather than passing it on.
        if (hasContentLength && hasTransferEncoding) throw new HttpParseException("both Content-Length and Transfer-Encoding");
        return new ProxyRequest(method, target, version, headers, remainder);
    }

    /// <summary>Visible ASCII only (0x21..0x7E): no spaces, no control characters and no bytes >= 0x80.</summary>
    private static bool IsVisibleAscii(string text)
    {
        foreach (var c in text)
        {
            if (c <= ' ' || c > '~') return false;
        }
        return true;
    }

    /// <summary>A header value safe to pass through: no control characters (HTAB permitted) and no DEL; obs-text (>= 0x80) is allowed.</summary>
    private static bool IsFieldValueSafe(string value)
    {
        foreach (var c in value)
        {
            if ((c < 0x20 && c != '\t') || c == 0x7F) return false;
        }
        return true;
    }

    /// <summary>host[:port] or [v6]:port or [v6]. The host comes back without brackets. A port outside 1..65535 = a failure.</summary>
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
        if (a.IndexOf(':', colon + 1) >= 0) return false; // IPv6 without brackets, or a malformed form
        host = a[..colon];
        return host.Length > 0 && !host.Contains('/') && TryParsePort(a[(colon + 1)..], out port);
    }

    /// <summary>An absolute-URI with the http scheme only. The host without brackets; the path starts with '/'.</summary>
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
