using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Josour.Core.Net;
using Josour.Tunnel.Mux;

namespace Josour.Proxy;

/// <summary>رد الـ Proxy الأعلى على CONNECT: الحالة، ترويسات المصادقة إن طُلبت، وأي بايتات تلت الرأس.</summary>
public sealed record UpstreamConnectResult(
    SocketStream? Stream,
    int Status,
    IReadOnlyList<string> ProxyAuthenticate,
    byte[] Remainder)
{
    public bool IsEstablished => Stream is not null;
}

/// <summary>
/// التخاطب مع Proxy النظام على المسار المباشر (الخطة 8.5: «المستخدم على شبكة شركة بـ Proxy إجباري …
/// المسار المباشر يمرر CONNECT إلى Proxy النظام»).
///
/// <para>لا يمس مسار القائمة المسموحة إطلاقًا: ذاك يمر عبر الـ mux إلى المضيف، ولا علاقة لـ Proxy جهاز
/// المستخدم به.</para>
/// </summary>
public static class UpstreamProxyClient
{
    /// <summary>أقصى رأس رد نقبله من الـ Proxy الأعلى.</summary>
    public const int MaxHeadBytes = 16 * 1024;

    /// <summary>
    /// يفتح مقبسًا إلى الـ Proxy الأعلى. عنوان الـ Proxy من إعداد الجهاز لا من الطلب، لذلك <b>لا</b> يخضع
    /// لفحص العناوين الخاصة: Proxy الشركات يسكن عادةً 10.x أو 127.0.0.1 وهذا هو الوضع الطبيعي.
    /// </summary>
    public static async Task<SocketStream?> DialAsync(SystemProxyEndpoint proxy, IHostResolver resolver, TimeSpan timeout, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(proxy);
        ArgumentNullException.ThrowIfNull(resolver);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        Socket? socket = null;
        try
        {
            IPAddress[] addresses = IPAddress.TryParse(proxy.Host, out var literal)
                ? new[] { literal }
                : await resolver.ResolveAsync(proxy.Host, cts.Token).ConfigureAwait(false);
            addresses = addresses.Where(a => a is not null && a.AddressFamily is AddressFamily.InterNetwork or AddressFamily.InterNetworkV6).ToArray();
            if (addresses.Length == 0) return null;
            var needV6 = addresses.Any(a => a.AddressFamily == AddressFamily.InterNetworkV6);
            var needV4 = addresses.Any(a => a.AddressFamily == AddressFamily.InterNetwork);
            socket = needV6
                ? new Socket(AddressFamily.InterNetworkV6, SocketType.Stream, ProtocolType.Tcp) { DualMode = needV4 }
                : new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            await socket.ConnectAsync(addresses, proxy.Port, cts.Token).ConfigureAwait(false);
            return new SocketStream(socket);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            socket?.Dispose();
            throw;
        }
        catch (Exception)
        {
            socket?.Dispose();
            return null;
        }
    }

    /// <summary>
    /// CONNECT إلى الـ Proxy الأعلى لوجهة <paramref name="host"/>:<paramref name="port"/>.
    /// <paramref name="proxyAuthorization"/> يُمرَّر كما أرسله المتصفح (يمكّن Basic/Digest عبر جولة 407 واحدة).
    /// عند غير 2xx يُغلق المقبس ويعاد الحالة مع ترويسات <c>Proxy-Authenticate</c> لتُنقل إلى المتصفح.
    /// </summary>
    public static async Task<UpstreamConnectResult> ConnectAsync(
        SocketStream upstream,
        string host,
        int port,
        string? proxyAuthorization,
        TimeSpan headTimeout,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(upstream);
        var authority = host + ":" + port.ToString(CultureInfo.InvariantCulture);
        var head = new StringBuilder()
            .Append("CONNECT ").Append(authority).Append(" HTTP/1.1\r\n")
            .Append("Host: ").Append(authority).Append("\r\n");
        if (!string.IsNullOrEmpty(proxyAuthorization)) head.Append("Proxy-Authorization: ").Append(proxyAuthorization).Append("\r\n");
        head.Append("Proxy-Connection: keep-alive\r\n\r\n");

        try
        {
            await upstream.WriteAsync(Encoding.Latin1.GetBytes(head.ToString()), ct).ConfigureAwait(false);
            await upstream.FlushAsync(ct).ConfigureAwait(false);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(headTimeout);
            var (status, authenticate, remainder) = await ReadHeadAsync(upstream, cts.Token).ConfigureAwait(false);
            if (status is >= 200 and < 300) return new UpstreamConnectResult(upstream, status, authenticate, remainder);
            upstream.Dispose();
            return new UpstreamConnectResult(null, status, authenticate, Array.Empty<byte>());
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            upstream.Dispose();
            throw;
        }
        catch (Exception)
        {
            upstream.Dispose();
            return new UpstreamConnectResult(null, 502, Array.Empty<string>(), Array.Empty<byte>());
        }
    }

    /// <summary>يقرأ رأس رد HTTP حتى CRLFCRLF ويعيد (الحالة، Proxy-Authenticate، ما تلا الرأس).</summary>
    private static async Task<(int Status, IReadOnlyList<string> Authenticate, byte[] Remainder)> ReadHeadAsync(Stream stream, CancellationToken ct)
    {
        var buffer = new byte[MaxHeadBytes];
        var filled = 0;
        while (true)
        {
            var index = buffer.AsSpan(0, filled).IndexOf("\r\n\r\n"u8);
            if (index >= 0)
            {
                var text = Encoding.Latin1.GetString(buffer, 0, index);
                var remainder = buffer.AsSpan(index + 4, filled - index - 4).ToArray();
                return ParseHead(text, remainder);
            }
            if (filled == buffer.Length) throw new HttpParseException("upstream proxy response head exceeds 16 KiB");
            var n = await stream.ReadAsync(buffer.AsMemory(filled), ct).ConfigureAwait(false);
            if (n == 0) throw new EndOfStreamException("upstream proxy closed before responding");
            filled += n;
        }
    }

    private static (int Status, IReadOnlyList<string> Authenticate, byte[] Remainder) ParseHead(string text, byte[] remainder)
    {
        var lines = text.Split("\r\n");
        var parts = lines[0].Split(' ', 3);
        if (parts.Length < 2 || !parts[0].StartsWith("HTTP/", StringComparison.Ordinal)
            || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var status))
            throw new HttpParseException("upstream proxy sent a malformed status line");

        var authenticate = new List<string>();
        foreach (var line in lines.Skip(1))
        {
            var colon = line.IndexOf(':');
            if (colon <= 0) continue;
            if (!line.AsSpan(0, colon).Trim().Equals("Proxy-Authenticate", StringComparison.OrdinalIgnoreCase)) continue;
            var value = line[(colon + 1)..].Trim();
            // ترويسة تُعاد كتابتها نحو المتصفح: لا محارف تحكم.
            if (value.Length > 0 && !value.Any(char.IsControl)) authenticate.Add(value);
        }
        return (status, authenticate, remainder);
    }
}
