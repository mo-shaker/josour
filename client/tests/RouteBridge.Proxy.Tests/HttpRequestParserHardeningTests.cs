using System.Diagnostics;
using System.Text;
using RouteBridge.Core.Allowlist;
using RouteBridge.Core.Net;

namespace RouteBridge.Proxy.Tests;

/// <summary>
/// تقوية المحلل (الأسبوع 4). العقد الذي تثبته هذه الاختبارات ثلاثة بنود، وكلها أمنية لا تجميلية:
///
/// <list type="number">
///   <item><b>لا يرمي شيئًا غير متوقَّع:</b> كل مدخل، مهما كان تالفًا، ينتهي إما بـ <see cref="ProxyRequest"/> أو
///     <see cref="HttpParseException"/> (يترجمها <see cref="ConnectProxyServer"/> إلى 400). أي استثناء آخر يقتل
///     معالج الاتصال ويظهر عدّاد <c>Errors</c> بدل رد صادق.</item>
///   <item><b>لا يعلّق:</b> رأس ناقص أو موزّع على قراءات بايت واحد ينتهي بمهلة أو باستثناء، لا بانتظار أبدي.</item>
///   <item><b>لا يمرّر ما يجب رفضه:</b> ما يخرج من المحلل يُعاد بناؤه حرفيًا نحو الأصل، فأي CR/LF داخل هدف أو قيمة
///     رأس يعني طلبًا ثانيًا مهرَّبًا. والهدف الناتج لا يجوز أن يوجّه إلى النفق إن كان عنوانًا حرفيًا أو اسمًا محليًا.</item>
/// </list>
/// </summary>
public class HttpRequestParserHardeningTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);
    private static readonly int[] DefaultPorts = { 80, 443 };

    // ---------- 1. حقن الرؤوس وتهريب الطلبات ----------

    [Theory]
    // LF مجرّد داخل قيمة رأس: عندنا سطر واحد، وعند الأصل سطران.
    [InlineData("GET http://a.example/ HTTP/1.1\r\nHost: a.example\r\nX: b\nHost: evil.example\r\n\r\n")]
    [InlineData("GET http://a.example/ HTTP/1.1\r\nX: b\nGET /admin HTTP/1.1\r\n\r\n")]
    // CR مجرّد داخل قيمة رأس.
    [InlineData("GET http://a.example/ HTTP/1.1\r\nX: b\rHost: evil.example\r\n\r\n")]
    // NUL داخل قيمة رأس (قطع عند مكتبات C).
    [InlineData("GET http://a.example/ HTTP/1.1\r\nX: a\0b\r\n\r\n")]
    // LF مجرّد داخل الهدف: parts.Length يبقى 3 والهدف يحمل سطرًا كاملًا.
    [InlineData("GET http://a.example/\nX-Injected:\x20yes HTTP/1.1\r\n\r\n")]
    // LF مجرّد داخل الإصدار: يبدأ بـ HTTP/1. ومع ذلك يحمل سطرًا.
    [InlineData("GET /p HTTP/1.1\nX-Injected: yes\r\nHost: a.example\r\n\r\n")]
    // obs-fold: سطر متابعة يبدأ بمسافة/HTAB — الوسطاء تختلف في طيّه (ناقل تهريب معروف).
    [InlineData("GET http://a.example/ HTTP/1.1\r\nX: b\r\n Host: evil.example\r\n\r\n")]
    [InlineData("GET http://a.example/ HTTP/1.1\r\nX: b\r\n\tevil\r\n\r\n")]
    // مسافة قبل النقطتين: "Foo " عند طرف و"Foo" عند آخر.
    [InlineData("GET http://a.example/ HTTP/1.1\r\nContent-Length : 5\r\n\r\n")]
    [InlineData("GET http://a.example/ HTTP/1.1\r\nHost\t: a.example\r\n\r\n")]
    // Host مكرَّر: نوجّه بالأول ويقرأ الأصل الأخير.
    [InlineData("GET / HTTP/1.1\r\nHost: a.example\r\nHost: evil.example\r\n\r\n")]
    [InlineData("CONNECT a.example:443 HTTP/1.1\r\nHost: a.example:443\r\nhost: evil.example:443\r\n\r\n")]
    // Content-Length مع Transfer-Encoding: طول غامض (RFC 7230 §3.3.3).
    [InlineData("POST http://a.example/ HTTP/1.1\r\nHost: a.example\r\nContent-Length: 5\r\nTransfer-Encoding: chunked\r\n\r\n0\r\n\r\n")]
    [InlineData("POST http://a.example/ HTTP/1.1\r\nTransfer-Encoding: chunked\r\ncontent-length: 0\r\n\r\n")]
    public void SmugglingVectors_AreRejected(string head)
    {
        Assert.Throws<HttpParseException>(() => HttpRequestParser.Parse(Encoding.Latin1.GetBytes(head), Array.Empty<byte>()));
    }

    [Fact]
    public void AbsurdHeaderCount_IsRejected_BeforeTheHeadLimit()
    {
        // 16 KiB تكفي لآلاف الرؤوس القصيرة؛ الحد على العدد هو ما يوقفها.
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
    // obs-text (≥ 0x80) مسموح في القيم كما في RFC 7230؛ الرفض يقتصر على محارف التحكم.
    [InlineData("GET http://a.example/ HTTP/1.1\r\nHost: a.example\r\nX: café\r\n\r\n")]
    // HTAB داخل القيمة مسموح.
    [InlineData("GET http://a.example/ HTTP/1.1\r\nHost: a.example\r\nX: a\tb\r\n\r\n")]
    // قيمة فارغة مسموحة.
    [InlineData("GET http://a.example/ HTTP/1.1\r\nHost: a.example\r\nX:\r\n\r\n")]
    public void LegitimateOddities_AreStillAccepted(string head)
    {
        var request = HttpRequestParser.Parse(Encoding.Latin1.GetBytes(head), Array.Empty<byte>());
        AssertNoInjectedLines(request, "/");
    }

    // ---------- 2. لا يرمي شيئًا غير متوقَّع، مهما كان المدخل ----------

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

            // قُبل: إذًا لا سطر مهرَّب في إعادة البناء، ولا توجيه إلى النفق لما يجب رفضه.
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
            // كل بايت في قراءة مستقلة: أسوأ حالة للتقسيم عبر عدة قراءات.
            using var stream = new TrickleStream(head);
            var expected = Outcome(() => HttpRequestParser.Parse(head, Array.Empty<byte>()));
            var actual = await OutcomeAsync(() => HttpRequestParser.ReadAsync(stream, CancellationToken.None).WaitAsync(Timeout));

            // الفارق الوحيد المسموح: رأس ناقص (بلا CRLFCRLF) — Parse يقبله على أنه كل ما وصل بينما ReadAsync ينتظر EOF ثم يشكو.
            if (expected == actual) continue;
            Assert.Equal("HttpParseException", actual);
        }

        Assert.True(clock.Elapsed < TimeSpan.FromMinutes(1), $"the trickle corpus took {clock.Elapsed}");
    }

    [Fact]
    public async Task ReadAsync_HeadSplitIntoSingleBytes_ParsesAndLosesNoBody()
    {
        // العقد: Remainder هو ما وصل في القراءة نفسها فقط؛ الباقي يبقى في الـ stream ليضخه الـ Proxy.
        // ما يجب ألا يحدث أبدًا: ضياع بايت من الجسم أو ابتلاع بايت زائد مع الرأس.
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
        // يثبت أن الانتظار محكوم بالـ token لا بحسن نية الطرف الآخر (الـ Proxy يربطه بـ RequestHeadTimeout).
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
        // CRLFCRLF موزّعة على عدة قراءات: يثبت تداخل نافذة البحث (scanFrom - 3) لكل محاذاة.
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

    // ---------- 3. الصيغ التي يرسلها المتصفح فعلًا ----------

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
        // عنوان حرفي: مرفوض محليًا قبل أي حل أو اتصال، ولا يُدرج في القائمة أصلًا.
        var route = ProxyRouter.Decide(parsedHost, parsedPort, EverythingAllowlist, DefaultPorts);
        Assert.Equal(RouteKind.Reject, route.Kind);
        Assert.Equal("ip_literal", route.Reason);
    }

    [Theory]
    // شكل absolute-URI بمضيف حرفي: لا يوجّه أيضًا.
    [InlineData("http://127.0.0.1/x")]
    [InlineData("http://[::1]/x")]
    [InlineData("http://2130706433/x")]      // 127.0.0.1 بالصيغة العشرية
    [InlineData("http://0x7f.0.0.1/x")]
    [InlineData("http://127.1/x")]
    public void AbsoluteUriWithLiteralHost_NeverRoutesToTheTunnel(string target)
    {
        if (!HttpRequestParser.TryParseHttpUri(target, out var host, out var port, out _)) return;
        Assert.NotEqual(RouteKind.Tunnel, ProxyRouter.Decide(host, port, EverythingAllowlist, DefaultPorts).Kind);
    }

    [Theory]
    [InlineData("GET / HTTP/1.1\r\n\r\n")]                                     // origin-form بلا Host
    [InlineData("GET / HTTP/1.1\r\nHost:\r\n\r\n")]                            // Host فارغ
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

    // ---------- الثوابت ----------

    /// <summary>قائمة تسمح بكل ما يمكن أن يُدرج: تجعل "لم يوجَّه إلى النفق" نتيجة السياسة لا نتيجة قائمة فارغة.</summary>
    private static readonly IAllowlist EverythingAllowlist = AllowlistMatcher.Parse(1, new[] { "example", "com", "net", "org", "test", "localhost", "local" });

    /// <summary>ما يخرج من إعادة البناء يجب أن يحمل سطرًا واحدًا لكل رأس أبقيناه، لا أكثر.</summary>
    private static void AssertNoInjectedLines(ProxyRequest request, string path)
    {
        var head = Encoding.Latin1.GetString(ConnectProxyServer.BuildOriginFormHead(request, path));
        var lines = head.Split("\r\n");
        // سطر الطلب + الرؤوس المُبقاة + "Connection: close" + سطران فارغان من النهاية.
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

    /// <summary>هدف مقبول لا يجوز أن يصير Tunnel إن كان عنوانًا حرفيًا أو اسمًا محليًا أو اسمًا لا يُطبَّع.</summary>
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
        Assert.Equal(route.Host, AllowlistMatcher.NormalizeHost(route.Host)); // مطبَّع ومستقر
    }

    // ---------- مولّد المدخلات ----------

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
                case 0: // بايتات عشوائية بالكامل
                    bytes = new byte[random.Next(0, 400)];
                    random.NextBytes(bytes);
                    break;
                case 1: // بذرة صحيحة + طفرات بايت
                    bytes = Encoding.Latin1.GetBytes(seeds[random.Next(seeds.Length)]);
                    for (var m = random.Next(1, 6); m > 0 && bytes.Length > 0; m--)
                        bytes[random.Next(bytes.Length)] = nasty[random.Next(nasty.Length)];
                    break;
                case 2: // بذرة صحيحة + إدراج محارف خبيثة
                {
                    var text = seeds[random.Next(seeds.Length)];
                    var at = random.Next(text.Length);
                    var injected = new[] { "\r\n", "\n", "\r", "\0", "\r\n ", "\r\n\t", " : ", "%00", "\r\nHost: evil.example" }[random.Next(9)];
                    bytes = Encoding.Latin1.GetBytes(text[..at] + injected + text[at..]);
                    break;
                }
                default: // رأس مبني من قطع عشوائية
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

    /// <summary>يعيد بايتًا واحدًا لكل قراءة ثم EOF.</summary>
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

    /// <summary>يعيد <c>chunk</c> بايتات لكل قراءة ثم EOF.</summary>
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

    /// <summary>لا يعيد شيئًا أبدًا ولا يغلق: يمثل نظيرًا يفتح الاتصال ثم يصمت.</summary>
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
