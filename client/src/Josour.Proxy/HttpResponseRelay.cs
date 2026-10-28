using System.Text;

namespace Josour.Proxy;

/// <summary>
/// Relaying an origin's reply to a plain-http request with <c>Connection: close</c> forced on it.
///
/// <para>
/// Why it exists: the local proxy carries one request per connection and then pumps bytes until the origin closes. A browser
/// pools its connections <b>to the proxy</b>, not to each origin, so a reply that looks reusable (HTTP/1.1 with no
/// <c>Connection: close</c>) lets the browser send its next plain-http request — for any site, with that site's cookies — down the
/// same socket, where it would be pumped raw to the first origin. A hostile origin only has to ignore our <c>Connection: close</c>
/// and keep the socket open. Rewriting every final reply head to say <c>Connection: close</c> and <c>Proxy-Connection: close</c>
/// stops the browser from reusing the connection at all.
/// </para>
/// <para>
/// Interim replies (1xx other than 101) pass through unchanged and the next head is read; <c>101 Switching Protocols</c> passes through
/// unchanged too, since the connection then belongs to the upgraded protocol and is never reused for HTTP.
/// </para>
/// </summary>
public static class HttpResponseRelay
{
    /// <summary>The largest reply head we accept from an origin (headers are larger on the way back than on the way out: cookies, CSP).</summary>
    public const int MaxHeadBytes = 64 * 1024;

    /// <summary>
    /// Reads the origin's reply heads, writes them to the client (the final one rewritten), then any body bytes already read.
    /// false = the origin closed before sending a single byte. Throws <see cref="HttpParseException"/> on a malformed or oversized head
    /// (in which case nothing of that head was written).
    /// </summary>
    public static async Task<bool> RelayHeadsAsync(Stream origin, Stream client, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(origin);
        ArgumentNullException.ThrowIfNull(client);
        var buffer = new byte[MaxHeadBytes];
        var filled = 0;
        var anyHead = false;
        while (true)
        {
            var end = FindHeadEnd(buffer.AsSpan(0, filled));
            if (end < 0)
            {
                if (filled == buffer.Length) throw new HttpParseException("response head exceeds 64 KiB");
                var n = await origin.ReadAsync(buffer.AsMemory(filled), ct).ConfigureAwait(false);
                if (n == 0)
                {
                    if (filled == 0 && !anyHead) return false;
                    throw new HttpParseException("origin closed before the response head completed");
                }
                filled += n;
                continue;
            }

            var head = Encoding.Latin1.GetString(buffer, 0, end);
            var status = ParseStatus(head);
            var interim = status is >= 100 and < 200 && status != 101;
            var output = interim || status == 101 ? buffer.AsMemory(0, end) : Rewrite(head);
            await client.WriteAsync(output, ct).ConfigureAwait(false);
            anyHead = true;

            if (!interim)
            {
                if (filled > end) await client.WriteAsync(buffer.AsMemory(end, filled - end), ct).ConfigureAwait(false);
                await client.FlushAsync(ct).ConfigureAwait(false);
                return true;
            }

            Buffer.BlockCopy(buffer, end, buffer, 0, filled - end);
            filled -= end;
        }
    }

    /// <summary>The index just past the blank line ending the head (CRLF CRLF, or a bare LF LF as lenient origins send it), or -1.</summary>
    public static int FindHeadEnd(ReadOnlySpan<byte> data)
    {
        for (var i = 0; i < data.Length; i++)
        {
            if (data[i] != (byte)'\n') continue;
            if (i + 1 < data.Length && data[i + 1] == (byte)'\n') return i + 2;
            if (i + 2 < data.Length && data[i + 1] == (byte)'\r' && data[i + 2] == (byte)'\n') return i + 3;
        }
        return -1;
    }

    private static int ParseStatus(string head)
    {
        var lineEnd = head.IndexOf('\n');
        var statusLine = (lineEnd < 0 ? head : head[..lineEnd]).TrimEnd('\r');
        var parts = statusLine.Split(' ', 3);
        if (parts.Length < 2 || !parts[0].StartsWith("HTTP/1.", StringComparison.Ordinal)
            || parts[1].Length != 3 || !parts[1].All(char.IsAsciiDigit))
            throw new HttpParseException("origin sent a malformed status line");
        return int.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// The final head with <c>Connection</c>, <c>Keep-Alive</c>, <c>Proxy-Connection</c> and every header the origin's
    /// <c>Connection</c> names (they are hop-by-hop) removed, and <c>Connection: close</c> + <c>Proxy-Connection: close</c> added.
    /// The line endings become CRLF; an obs-folded continuation stays with (or goes with) the header it continues.
    /// </summary>
    public static byte[] Rewrite(string head)
    {
        var lines = head.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        while (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);

        var hopByHop = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Connection", "Keep-Alive", "Proxy-Connection" };
        foreach (var line in lines.Skip(1))
        {
            var colon = line.IndexOf(':');
            if (colon <= 0 || line[0] is ' ' or '\t') continue;
            if (!line[..colon].Trim().Equals("Connection", StringComparison.OrdinalIgnoreCase)) continue;
            foreach (var token in line[(colon + 1)..].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                hopByHop.Add(token);
        }

        var sb = new StringBuilder(head.Length + 48);
        sb.Append(lines[0]).Append("\r\n");
        var dropping = false;
        foreach (var line in lines.Skip(1))
        {
            if (line.Length > 0 && line[0] is ' ' or '\t')
            {
                if (!dropping) sb.Append(line).Append("\r\n");
                continue;
            }
            var colon = line.IndexOf(':');
            dropping = colon > 0 && hopByHop.Contains(line[..colon].Trim());
            if (!dropping) sb.Append(line).Append("\r\n");
        }
        sb.Append("Connection: close\r\nProxy-Connection: close\r\n\r\n");
        return Encoding.Latin1.GetBytes(sb.ToString());
    }
}
