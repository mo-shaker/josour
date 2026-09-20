using System.Diagnostics;
using System.Text;
using Josour.Core.Allowlist;
using Josour.Core.Net;

namespace Josour.Proxy.Tests;

/// <summary>
/// Hardening the parser (week 4). The contract these tests prove has three items, all of them security rather than cosmetics:
///
/// <list type="number">
///   <item><b>It throws nothing unexpected:</b> every input, however malformed, ends either as a <see cref="ProxyRequest"/> or as an
///     <see cref="HttpParseException"/> (which <see cref="ConnectProxyServer"/> translates into a 400). Any other exception kills
///     the connection handler and shows up in the <c>Errors</c> counter instead of a truthful reply.</item>
///   <item><b>It does not hang:</b> a truncated head, or one spread over single-byte reads, ends in a timeout or an exception, never in an endless wait.</item>
///   <item><b>It does not pass on what must be refused:</b> what comes out of the parser is rebuilt literally towards the origin, so any CR/LF inside a target or a header
///     value means a second smuggled request. And the resulting target may not be routed to the tunnel if it is an address literal or a local name.</item>
/// </list>
/// </summary>
public class HttpRequestParserHardeningTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);
    private static readonly int[] DefaultPorts = { 80, 443 };

    // ---------- 1. Header injection and request smuggling ----------

    [Theory]
    // A bare LF inside a header value: one line here, and two at the origin.
    [InlineData("GET http://a.example/ HTTP/1.1\r\nHost: a.example\r\nX: b\nHost: evil.example\r\n\r\n")]
    [InlineData("GET http://a.example/ HTTP/1.1\r\nX: b\nGET /admin HTTP/1.1\r\n\r\n")]
    // A bare CR inside a header value.
    [InlineData("GET http://a.example/ HTTP/1.1\r\nX: b\rHost: evil.example\r\n\r\n")]
    // A NUL inside a header value (a truncation point in C libraries).
    [InlineData("GET http://a.example/ HTTP/1.1\r\nX: a\0b\r\n\r\n")]
    // A bare LF inside the target: parts.Length stays 3 and the target carries a whole line.
    [InlineData("GET http://a.example/\nX-Injected:\x20yes HTTP/1.1\r\n\r\n")]
    // A bare LF inside the version: it starts with HTTP/1. and still carries a line.
    [InlineData("GET /p HTTP/1.1\nX-Injected: yes\r\nHost: a.example\r\n\r\n")]
    // obs-fold: a continuation line starting with a space/HTAB — intermediaries differ on folding it (a known smuggling vector).
    [InlineData("GET http://a.example/ HTTP/1.1\r\nX: b\r\n Host: evil.example\r\n\r\n")]
    [InlineData("GET http://a.example/ HTTP/1.1\r\nX: b\r\n\tevil\r\n\r\n")]
    // A space before the colon: "Foo " at one end and "Foo" at the other.
    [InlineData("GET http://a.example/ HTTP/1.1\r\nContent-Length : 5\r\n\r\n")]
    [InlineData("GET http://a.example/ HTTP/1.1\r\nHost\t: a.example\r\n\r\n")]
    // A repeated Host: we route by the first and the origin reads the last.
    [InlineData("GET / HTTP/1.1\r\nHost: a.example\r\nHost: evil.example\r\n\r\n")]
    [InlineData("CONNECT a.example:443 HTTP/1.1\r\nHost: a.example:443\r\nhost: evil.example:443\r\n\r\n")]
    // Content-Length together with Transfer-Encoding: an ambiguous length (RFC 7230 §3.3.3).
    [InlineData("POST http://a.example/ HTTP/1.1\r\nHost: a.example\r\nContent-Length: 5\r\nTransfer-Encoding: chunked\r\n\r\n0\r\n\r\n")]
    [InlineData("POST http://a.example/ HTTP/1.1\r\nTransfer-Encoding: chunked\r\ncontent-length: 0\r\n\r\n")]
    public void SmugglingVectors_AreRejected(string head)
    {
        Assert.Throws<HttpParseException>(() => HttpRequestParser.Parse(Encoding.Latin1.GetBytes(head), Array.Empty<byte>()));
    }

    [Fact]
    public void AbsurdHeaderCount_IsRejected_BeforeTheHeadLimit()
    {
        // 16 KiB is enough for thousands of short headers; the limit on the count is what stops them.
        var head = new StringBuilder("GET http://a.example/ HTTP/1.1\r\n");
        for (var i = 0; i <= HttpRequestParser.MaxHeaders; i++) head.Append("h").Append(i).Append(":v\r\n");
        head.Append("\r\n");
        var bytes = Encoding.Latin1.GetBytes(head.ToString());
        Assert.True(bytes.Length < HttpRequestParser.MaxHeadBytes, "the corpus must fit inside the head limit so the header count is what rejects it");
        Assert.Throws<HttpParseException>(() => HttpRequestParser.Parse(bytes, Array.Empty<byte>()));
    }

    [Fact]
    public void HeaderCountAtTheLimit_IsAccepted()
    {
        var head = new StringBuilder("GET http://a.example/ HTTP/1.1\r\n");
        for (var i = 0; i < HttpRequestParser.MaxHeaders; i++) head.Append("h").Append(i).Append(":v\r\n");
        head.Append("\r\n");
        var request = HttpRequestParser.Parse(Encoding.Latin1.GetBytes(head.ToString()), Array.Empty<byte>());
        Assert.Equal(HttpRequestParser.MaxHeaders, request.Headers.Count);
    }

    [Fact]
    public void VeryLongHeaderValue_WithinTheHeadLimit_IsAccepted_AndSurvivesTheRewrite()
    {
        var value = new string('v', HttpRequestParser.MaxHeadBytes - 200);
        var request = HttpRequestParser.Parse(
            Encoding.Latin1.GetBytes($"GET http://a.example/ HTTP/1.1\r\nHost: a.example\r\nX: {value}\r\n\r\n"),
            Array.Empty<byte>());
        Assert.Equal(value, request.Header("X"));
        AssertNoInjectedLines(request, "/");
    }

    [Theory]
    // obs-text (>= 0x80) is allowed in values as in RFC 7230; the refusal is limited to control characters.
    [InlineData("GET http://a.example/ HTTP/1.1\r\nHost: a.example\r\nX: café\r\n\r\n")]
    // An HTAB inside the value is allowed.
    [InlineData("GET http://a.example/ HTTP/1.1\r\nHost: a.example\r\nX: a\tb\r\n\r\n")]
    // An empty value is allowed.
    [InlineData("GET http://a.example/ HTTP/1.1\r\nHost: a.example\r\nX:\r\n\r\n")]
    public void LegitimateOddities_AreStillAccepted(string head)
    {
        var request = HttpRequestParser.Parse(Encoding.Latin1.GetBytes(head), Array.Empty<byte>());
        AssertNoInjectedLines(request, "/");
    }

    // ---------- 2. It throws nothing unexpected, whatever the input ----------

    [Fact]
    public void Fuzz_Parse_OnlyEverThrowsHttpParseException()
    {
        var checkedInputs = 0;
        foreach (var head in Corpus(seed: 20260905, count: 4000))
        {
            checkedInputs++;
            ProxyRequest? request = null;
            try
            {
                request = HttpRequestParser.Parse(head, Array.Empty<byte>());
            }
            catch (HttpParseException)
            {
                continue;
            }
            catch (Exception e)
            {
                Assert.Fail($"unexpected {e.GetType().Name} for {Describe(head)}: {e.Message}");
            }

            // Accepted: so there is no smuggled line in the rebuild, and nothing that must be refused is routed to the tunnel.
            AssertNoInjectedLines(request!, "/");
            AssertRoutingIsSafe(request!);
        }

        Assert.Equal(4000, checkedInputs);
    }

    [Fact]
    public async Task Fuzz_ReadAsync_MatchesParse_AndNeverHangs()
    {
        var clock = Stopwatch.StartNew();
        foreach (var head in Corpus(seed: 7, count: 300))
        {
            // Every byte in its own read: the worst case for splitting across several reads.
            using var stream = new TrickleStream(head);
            var expected = Outcome(() => HttpRequestParser.Parse(head, Array.Empty<byte>()));
            var actual = await OutcomeAsync(() => HttpRequestParser.ReadAsync(stream, CancellationToken.None).WaitAsync(Timeout));

            // The one permitted difference: a truncated head (with no CRLFCRLF) — Parse accepts it as everything that arrived, while ReadAsync waits for EOF and then complains.
            if (expected == actual) continue;
            Assert.Equal("HttpParseException", actual);
        }

        Assert.True(clock.Elapsed < TimeSpan.FromMinutes(1), $"the trickle corpus took {clock.Elapsed}");
    }

    [Fact]
    public async Task ReadAsync_HeadSplitIntoSingleBytes_ParsesAndLosesNoBody()
    {
        // The contract: Remainder is only what arrived in the same read; the rest stays in the stream for the proxy to pump.
        // What must never happen: losing a byte of the body, or swallowing an extra byte with the head.
        const string head = "CONNECT a.example:443 HTTP/1.1\r\nHost: a.example:443\r\n\r\n";
        const string body = "\x16\x03\x01tls";
        using var stream = new TrickleStream(Encoding.Latin1.GetBytes(head + body));
        var request = await HttpRequestParser.ReadAsync(stream, CancellationToken.None).WaitAsync(Timeout);
        Assert.NotNull(request);
        Assert.True(request!.IsConnect);
        Assert.Equal("a.example:443", request.Target);
        Assert.Equal(body, Encoding.Latin1.GetString(request.Remainder) + await ReadToEndAsync(stream));
    }

    [Fact]
    public async Task ReadAsync_SlowStreamThatNeverCompletesTheHead_IsCancellable()
    {
        // It proves the wait is governed by the token rather than by the other side's good faith (the proxy binds it to RequestHeadTimeout).
        using var stalled = new StalledStream();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => HttpRequestParser.ReadAsync(stalled, cts.Token).WaitAsync(Timeout));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(7)]
    public async Task ReadAsync_HeadEndSplitAcrossReads_IsFound_AndTheBodyIsIntact(int chunk)
    {
        // A CRLFCRLF spread over several reads: it proves the search window overlaps (scanFrom - 3) for every alignment.
        var bytes = Encoding.Latin1.GetBytes("GET http://a.example/ HTTP/1.1\r\nHost: a.example\r\n\r\nbody");
        using var stream = new ChunkedStream(bytes, chunk);
        var request = await HttpRequestParser.ReadAsync(stream, CancellationToken.None).WaitAsync(Timeout);
        Assert.NotNull(request);
        Assert.Equal("http://a.example/", request!.Target);
        Assert.Equal("body", Encoding.Latin1.GetString(request.Remainder) + await ReadToEndAsync(stream));
    }

    private static async Task<string> ReadToEndAsync(Stream stream)
    {
        var buffer = new byte[256];
        var sb = new StringBuilder();
        int n;
        while ((n = await stream.ReadAsync(buffer).AsTask().WaitAsync(Timeout)) > 0) sb.Append(Encoding.Latin1.GetString(buffer, 0, n));
        return sb.ToString();
    }

    // ---------- 3. The forms the browser actually sends ----------

    [Theory]
    [InlineData("CONNECT [2001:db8::1]:443 HTTP/1.1\r\n\r\n", "2001:db8::1", 443)]
    [InlineData("CONNECT [::ffff:127.0.0.1]:443 HTTP/1.1\r\n\r\n", "::ffff:127.0.0.1", 443)]
    [InlineData("CONNECT [64:ff9b::7f00:1]:443 HTTP/1.1\r\n\r\n", "64:ff9b::7f00:1", 443)]
    public void BracketedIPv6_Parses_ButNeverRoutes(string head, string host, int port)
    {
        var request = HttpRequestParser.Parse(Encoding.Latin1.GetBytes(head), Array.Empty<byte>());
        Assert.True(HttpRequestParser.TryParseAuthority(request.Target, 443, out var parsedHost, out var parsedPort));
        Assert.Equal(host, parsedHost);
        Assert.Equal(port, parsedPort);
        // An address literal: refused locally before any resolution or connection, and it cannot be listed to begin with.
        var route = ProxyRouter.Decide(parsedHost, parsedPort, EverythingAllowlist, DefaultPorts);
        Assert.Equal(RouteKind.Reject, route.Kind);
        Assert.Equal("ip_literal", route.Reason);
    }

    [Theory]
    // An absolute-URI form with a literal host: also not routed.
    [InlineData("http://127.0.0.1/x")]
    [InlineData("http://[::1]/x")]
    [InlineData("http://2130706433/x")]      // 127.0.0.1 in decimal form
    [InlineData("http://0x7f.0.0.1/x")]
    [InlineData("http://127.1/x")]
    public void AbsoluteUriWithLiteralHost_NeverRoutesToTheTunnel(string target)
    {
        if (!HttpRequestParser.TryParseHttpUri(target, out var host, out var port, out _)) return;
        Assert.NotEqual(RouteKind.Tunnel, ProxyRouter.Decide(host, port, EverythingAllowlist, DefaultPorts).Kind);
    }

    [Theory]
    [InlineData("GET / HTTP/1.1\r\n\r\n")]                                     // origin-form with no Host
    [InlineData("GET / HTTP/1.1\r\nHost:\r\n\r\n")]                            // an empty Host
    [InlineData("GET / HTTP/1.1\r\nHost:    \r\n\r\n")]
    public void OriginFormWithoutUsableHost_YieldsNoAuthority(string head)
    {
        var request = HttpRequestParser.Parse(Encoding.Latin1.GetBytes(head), Array.Empty<byte>());
        var hostHeader = request.Header("Host");
        Assert.True(string.IsNullOrWhiteSpace(hostHeader) || !HttpRequestParser.TryParseAuthority(hostHeader, 80, out _, out _));
    }

    [Theory]
    [InlineData("0\r\n\r\n")]
    [InlineData("5\r\nhello\r\n0\r\n\r\n")]
    [InlineData("ffffffffffffffff\r\n")]
    [InlineData("-1\r\n")]
    public void ChunkedLookingBody_StaysInTheRemainder_AndIsNeverParsedAsHeaders(string body)
    {
        var head = Encoding.Latin1.GetBytes("POST http://a.example/ HTTP/1.1\r\nHost: a.example\r\nTransfer-Encoding: chunked\r\n\r\n");
        var request = HttpRequestParser.Parse(head, Encoding.Latin1.GetBytes(body));
        Assert.Equal(2, request.Headers.Count);
        Assert.Equal(body, Encoding.Latin1.GetString(request.Remainder));
        AssertNoInjectedLines(request, "/");
    }

    // ---------- The constants ----------

    /// <summary>A list that allows everything that could be listed: it makes "it was not routed to the tunnel" the policy's result rather than an empty list's.</summary>
    private static readonly IAllowlist EverythingAllowlist = AllowlistMatcher.Parse(1, new[] { "example", "com", "net", "org", "test", "localhost", "local" });

    /// <summary>What comes out of the rebuild must carry one line per header we kept, and no more.</summary>
    private static void AssertNoInjectedLines(ProxyRequest request, string path)
    {
        var head = Encoding.Latin1.GetString(ConnectProxyServer.BuildOriginFormHead(request, path));
        var lines = head.Split("\r\n");
        // The request line + the kept headers + "Connection: close" + two empty lines at the end.
        var kept = request.Headers.Count(h => !IsHopByHop(h.Key));
        Assert.Equal(kept + 4, lines.Length);
        foreach (var line in lines)
        {
            Assert.DoesNotContain('\r', line);
            Assert.DoesNotContain('\n', line);
            Assert.DoesNotContain('\0', line);
        }
    }

    private static bool IsHopByHop(string name)
        => name.Equals("Proxy-Connection", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Proxy-Authorization", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Connection", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Keep-Alive", StringComparison.OrdinalIgnoreCase);

    /// <summary>An acceptable target that may not become Tunnel if it is an address literal, a local name, or a name that does not normalise.</summary>
    private static void AssertRoutingIsSafe(ProxyRequest request)
    {
        string? host = null;
        var port = request.IsConnect ? 443 : 80;
        if (request.IsConnect)
        {
            if (!HttpRequestParser.TryParseAuthority(request.Target, 443, out var h, out var p)) return;
            host = h;
            port = p;
        }
        else if (HttpRequestParser.TryParseHttpUri(request.Target, out var h, out var p, out _))
        {
            host = h;
            port = p;
        }

        if (host is null) return;
        var route = ProxyRouter.Decide(host, port, EverythingAllowlist, DefaultPorts);
        if (route.Kind != RouteKind.Tunnel) return;

        Assert.False(IpRangePolicy.IsIpLiteral(host), $"an IP literal was routed to the tunnel: {host}");
        Assert.False(ProxyRouter.IsLocalName(route.Host), $"a local name was routed to the tunnel: {route.Host}");
        Assert.Equal(route.Host, AllowlistMatcher.NormalizeHost(route.Host)); // normalised and stable
    }

    // ---------- The input generator ----------

    private static IEnumerable<byte[]> Corpus(int seed, int count)
    {
        var random = new Random(seed);
        var seeds = new[]
        {
            "GET http://a.example/p?q=1 HTTP/1.1\r\nHost: a.example\r\nUser-Agent: x\r\n\r\n",
            "CONNECT a.example:443 HTTP/1.1\r\nHost: a.example:443\r\nProxy-Connection: keep-alive\r\n\r\n",
            "CONNECT [2001:db8::1]:443 HTTP/1.1\r\n\r\n",
            "POST http://a.example/ HTTP/1.0\r\nContent-Length: 3\r\n\r\nabc",
            "GET / HTTP/1.1\r\nHost: a.example\r\n\r\n",
            "OPTIONS * HTTP/1.1\r\nHost: a.example\r\n\r\n",
        };
        var nasty = new[] { (byte)'\r', (byte)'\n', (byte)0, (byte)' ', (byte)'\t', (byte)':', (byte)'[', (byte)']', (byte)'/', (byte)0x7F, (byte)0x80, (byte)0xFF };

        for (var i = 0; i < count; i++)
        {
            byte[] bytes;
            switch (i % 4)
            {
                case 0: // entirely random bytes
                    bytes = new byte[random.Next(0, 400)];
                    random.NextBytes(bytes);
                    break;
                case 1: // a valid seed + byte mutations
                    bytes = Encoding.Latin1.GetBytes(seeds[random.Next(seeds.Length)]);
                    for (var m = random.Next(1, 6); m > 0 && bytes.Length > 0; m--)
                        bytes[random.Next(bytes.Length)] = nasty[random.Next(nasty.Length)];
                    break;
                case 2: // a valid seed + inserting malicious characters
                {
                    var text = seeds[random.Next(seeds.Length)];
                    var at = random.Next(text.Length);
                    var injected = new[] { "\r\n", "\n", "\r", "\0", "\r\n ", "\r\n\t", " : ", "%00", "\r\nHost: evil.example" }[random.Next(9)];
                    bytes = Encoding.Latin1.GetBytes(text[..at] + injected + text[at..]);
                    break;
                }
                default: // a head built from random pieces
                {
                    var head = new StringBuilder();
                    head.Append(Token(random)).Append(' ').Append(Token(random)).Append(' ').Append(Token(random)).Append("\r\n");
                    for (var h = random.Next(0, 8); h > 0; h--)
                        head.Append(Token(random)).Append(':').Append(Token(random)).Append("\r\n");
                    head.Append("\r\n");
                    bytes = Encoding.Latin1.GetBytes(head.ToString());
                    break;
                }
            }

            yield return bytes;
        }
    }

    private static string Token(Random random)
    {
        var alphabet = "abcHTTP/1.0:.-_[]@ \t\r\n\0\x7fÿ*?&=%";
        var length = random.Next(0, 12);
        var sb = new StringBuilder(length);
        for (var i = 0; i < length; i++) sb.Append(alphabet[random.Next(alphabet.Length)]);
        return sb.ToString();
    }

    private static string Describe(byte[] head) => "0x" + Convert.ToHexString(head.AsSpan(0, Math.Min(head.Length, 120)));

    private static string Outcome(Func<ProxyRequest?> action)
    {
        try { return action() is { } r ? $"{r.Method}|{r.Target}|{r.Version}|{r.Headers.Count}" : "null"; }
        catch (Exception e) { return e.GetType().Name; }
    }

    private static async Task<string> OutcomeAsync(Func<Task<ProxyRequest?>> action)
    {
        try { return await action() is { } r ? $"{r.Method}|{r.Target}|{r.Version}|{r.Headers.Count}" : "null"; }
        catch (Exception e) { return e.GetType().Name; }
    }

    // ---------- streams ----------

    /// <summary>Returns one byte per read, then EOF.</summary>
    private sealed class TrickleStream : Stream
    {
        private readonly byte[] _data;
        private int _position;

        public TrickleStream(byte[] data) => _data = data;

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_position >= _data.Length || buffer.Length == 0) return ValueTask.FromResult(0);
            buffer.Span[0] = _data[_position++];
            return ValueTask.FromResult(1);
        }

        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _data.Length;
        public override long Position { get => _position; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>Returns <c>chunk</c> bytes per read, then EOF.</summary>
    private sealed class ChunkedStream : Stream
    {
        private readonly byte[] _data;
        private readonly int _chunk;
        private int _position;

        public ChunkedStream(byte[] data, int chunk)
        {
            _data = data;
            _chunk = chunk;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var n = Math.Min(Math.Min(_chunk, buffer.Length), _data.Length - _position);
            if (n <= 0) return ValueTask.FromResult(0);
            _data.AsSpan(_position, n).CopyTo(buffer.Span);
            _position += n;
            return ValueTask.FromResult(n);
        }

        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _data.Length;
        public override long Position { get => _position; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>It never returns anything and never closes: it stands in for a peer that opens the connection and then goes silent.</summary>
    private sealed class StalledStream : Stream
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(System.Threading.Timeout.Infinite, cancellationToken).ConfigureAwait(false);
            return 0;
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
