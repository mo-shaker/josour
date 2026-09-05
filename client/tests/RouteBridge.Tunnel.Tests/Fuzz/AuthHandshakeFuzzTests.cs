using System.Diagnostics;
using System.Security.Cryptography;
using RouteBridge.Core.Tunnel;
using RouteBridge.Tunnel.Auth;
using Xunit.Abstractions;

namespace RouteBridge.Tunnel.Tests.Fuzz;

/// <summary>
/// ‏stream يقرأ من مخزن جاهز ويحتفظ بكل ما يُكتب إليه: يجعل جانب المضيف من المصافحة قابلًا للقياس آلاف المرات
/// بلا مقابس، ويجعل «كم بايتًا كتب المضيف؟» سؤالًا يُجاب بدقة. نفاد المخزن = EOF (إغلاق الطرف الآخر).
/// </summary>
internal sealed class ReplayStream : Stream
{
    private readonly byte[] _source;
    private readonly MemoryStream _written = new();
    private int _position;

    public ReplayStream(byte[] source) => _source = source;

    /// <summary>ما كتبه الطرف المُختبَر. يجب أن يكون فارغًا في كل حالة فشل مصادقة.</summary>
    public byte[] Written => _written.ToArray();

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override int Read(byte[] buffer, int offset, int count)
    {
        var n = Math.Min(count, _source.Length - _position);
        if (n <= 0) return 0;
        _source.AsSpan(_position, n).CopyTo(buffer.AsSpan(offset, n));
        _position += n;
        return n;
    }

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var n = Math.Min(buffer.Length, _source.Length - _position);
        if (n <= 0) return ValueTask.FromResult(0);
        _source.AsSpan(_position, n).CopyTo(buffer.Span);
        _position += n;
        return ValueTask.FromResult(n);
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override void Write(byte[] buffer, int offset, int count) => _written.Write(buffer, offset, count);

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        _written.Write(buffer.Span);
        return ValueTask.CompletedTask;
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override void Flush() { }
    public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}

/// <summary>
/// ‏fuzz على <c>AUTH1</c>/<c>AUTH2</c> (<c>docs/protocol.md</c> القسم 4). القاعدة الحاكمة في العقد:
/// <b>«الفشل: إغلاق الاتصال بلا رد»</b> و«المضيف لا يكتب أي بايت قبل التحقق». الاختبار هنا يقيسها حرفيًا:
/// عدد البايتات التي كتبها المضيف بعد كل مدخل معادٍ يجب أن يكون صفرًا.
/// </summary>
public class AuthHandshakeFuzzTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);
    private readonly ITestOutputHelper _output;

    public AuthHandshakeFuzzTests(ITestOutputHelper output) => _output = output;

    private static (SessionMaterial Material, byte[] Fingerprint) Fixture(int seed)
    {
        var rng = FuzzSeed.For("auth-fixture", seed);
        var secret = new byte[AuthHandshake.SecretLength];
        rng.NextBytes(secret);
        var fingerprint = new byte[AuthHandshake.FingerprintLength];
        rng.NextBytes(fingerprint);
        var guid = new byte[16];
        rng.NextBytes(guid);
        var material = new SessionMaterial(new Guid(guid), TunnelRole.Host, secret, DateTimeOffset.UtcNow.AddMinutes(30), true, "203.0.113.1");
        return (material, fingerprint);
    }

    /// <summary>‏AUTH1 صحيح تمامًا: مرجع المقارنة، ويثبت أن أداة البناء في الاختبار تطابق العقد.</summary>
    private static byte[] ValidAuth1(SessionMaterial material, byte[] fingerprint, byte[]? clientRandom = null)
    {
        var sessionId = AuthHandshake.SessionIdBytes(material.SessionId);
        clientRandom ??= RandomNumberGenerator.GetBytes(AuthHandshake.RandomLength);
        var input = new byte[8 + 16 + 32 + 32];
        "rb-auth1"u8.CopyTo(input);
        sessionId.CopyTo(input, 8);
        clientRandom.CopyTo(input, 24);
        fingerprint.CopyTo(input, 56);
        var mac = HMACSHA256.HashData(material.Secret, input);

        var auth1 = new byte[AuthHandshake.Auth1Length];
        auth1[0] = AuthHandshake.Version;
        sessionId.CopyTo(auth1, 1);
        clientRandom.CopyTo(auth1, 17);
        mac.CopyTo(auth1, 49);
        return auth1;
    }

    private static async Task<(Exception? Error, byte[] Written)> HostAsync(byte[] input, SessionMaterial material, byte[] fingerprint)
    {
        var stream = new ReplayStream(input);
        try
        {
            await AuthHandshake.VerifyAuth1AndSendAuth2Async(stream, material, fingerprint, Timeout, CancellationToken.None);
            return (null, stream.Written);
        }
        catch (Exception e)
        {
            return (e, stream.Written);
        }
    }

    // ---------- المرجع ----------

    [Fact]
    public async Task ValidAuth1_IsAccepted_AndAnsweredWithExactly32Bytes()
    {
        var (material, fingerprint) = Fixture(0);
        var (error, written) = await HostAsync(ValidAuth1(material, fingerprint), material, fingerprint);
        Assert.Null(error);
        Assert.Equal(AuthHandshake.Auth2Length, written.Length);
    }

    // ---------- قطع ----------

    /// <summary>‏0..80 بايت ثم إغلاق: المضيف يفشل بلا رد مهما كان الطول.</summary>
    [Fact]
    public async Task TruncatedAuth1_AtEveryLength_IsRejectedSilently()
    {
        var (material, fingerprint) = Fixture(1);
        var valid = ValidAuth1(material, fingerprint);
        for (var length = 0; length < AuthHandshake.Auth1Length; length++)
        {
            var (error, written) = await HostAsync(valid[..length], material, fingerprint);
            Assert.NotNull(error);
            AssertExpectedAuthFailure(error!, $"truncated to {length} bytes");
            Assert.Empty(written);
        }
    }

    /// <summary>‏81 بايتًا صحيحة متبوعة بحشو: ما بعد الرسالة لا يُقرأ ولا يغيّر الحكم (لا تهريب حمولة).</summary>
    [Theory]
    [InlineData(1)]
    [InlineData(1024)]
    [InlineData(64 * 1024)]
    public async Task Auth1FollowedByExtraBytes_IsStillAccepted_AndTheExtraIsNotConsumed(int extra)
    {
        var (material, fingerprint) = Fixture(2);
        var valid = ValidAuth1(material, fingerprint);
        var padded = new byte[valid.Length + extra];
        valid.CopyTo(padded, 0);
        FuzzSeed.For("auth-pad", extra).NextBytes(padded.AsSpan(valid.Length));

        var (error, written) = await HostAsync(padded, material, fingerprint);

        Assert.Null(error);
        Assert.Equal(AuthHandshake.Auth2Length, written.Length);
    }

    /// <summary>‏AUTH1 مضخّم بلا مقدمة صحيحة: يُرفض بلا رد ولا تخصيص يتبع الطول المعلن (لا طول معلن أصلًا).</summary>
    [Fact]
    public async Task OversizedGarbage_IsRejectedSilently()
    {
        var (material, fingerprint) = Fixture(3);
        var garbage = Mutate.Random(FuzzSeed.For("auth-oversize", 0), 4 * 1024 * 1024);
        var before = GC.GetTotalMemory(forceFullCollection: true);
        var (error, written) = await HostAsync(garbage, material, fingerprint);
        var after = GC.GetTotalMemory(forceFullCollection: true);

        Assert.NotNull(error);
        AssertExpectedAuthFailure(error!, "4 MiB of garbage");
        Assert.Empty(written);
        Assert.True(after - before < 16L * 1024 * 1024, $"a 4 MiB garbage AUTH1 grew the heap by {(after - before) / 1024} KiB");
    }

    // ---------- قلب البتات ----------

    /// <summary>
    /// قلب بت واحد في كل موضع من الـ 81 بايتًا: كل واحدة ترفَض بلا رد. هذا يغطي كل حقول الرسالة معًا
    /// (الإصدار، معرّف الجلسة، العشوائي، الـ MAC) بلا استثناء.
    /// </summary>
    [Fact]
    public async Task EverySingleBitFlip_IsRejectedSilently()
    {
        var (material, fingerprint) = Fixture(4);
        var valid = ValidAuth1(material, fingerprint);
        var checkedBits = 0;
        for (var index = 0; index < valid.Length; index++)
        {
            for (var bit = 0; bit < 8; bit++)
            {
                var mutated = (byte[])valid.Clone();
                mutated[index] ^= (byte)(1 << bit);
                var (error, written) = await HostAsync(mutated, material, fingerprint);
                Assert.NotNull(error);
                Assert.IsType<AuthFailedException>(error);
                Assert.Empty(written);
                checkedBits++;
            }
        }
        _output.WriteLine($"every one of {checkedBits} single-bit flips in AUTH1 was rejected with zero bytes written");
        Assert.Equal(AuthHandshake.Auth1Length * 8, checkedBits);
    }

    /// <summary>بايتات عشوائية بأطوال عشوائية: لا نوع استثناء خارج المتوقَّع، ولا بايت واحد يُكتب.</summary>
    [Fact]
    public async Task RandomInput_NeverProducesAReply_NorAnUnexpectedExceptionType()
    {
        var (material, fingerprint) = Fixture(5);
        var cases = FuzzSeed.Cases(1500);
        for (var i = 0; i < cases; i++)
        {
            var rng = FuzzSeed.For("auth-random", i);
            var input = Mutate.Random(rng, rng.Next(0, 200));
            var (error, written) = await HostAsync(input, material, fingerprint);
            Assert.NotNull(error);
            AssertExpectedAuthFailure(error!, $"case {i} ({input.Length} bytes)");
            Assert.Empty(written);
        }
        _output.WriteLine($"{cases} random AUTH1 inputs: all rejected, none produced a byte on the wire");
    }

    /// <summary>سر خاطئ وبصمة خاطئة ومعرّف جلسة خاطئ، كلٌّ على حدة: العقد يعطي النتيجة نفسها لكلٍّ منها.</summary>
    [Fact]
    public async Task WrongSecret_WrongSessionId_AndWrongFingerprint_AllFailIdentically()
    {
        var (material, fingerprint) = Fixture(6);
        var (other, otherFingerprint) = Fixture(7);

        foreach (var (label, bytes) in new (string, byte[])[]
        {
            ("wrong secret", ValidAuth1(new SessionMaterial(material.SessionId, TunnelRole.Host, other.Secret, material.ExpiresAt, true, "203.0.113.1"), fingerprint)),
            ("wrong session id", ValidAuth1(new SessionMaterial(other.SessionId, TunnelRole.Host, material.Secret, material.ExpiresAt, true, "203.0.113.1"), fingerprint)),
            ("wrong listener fingerprint", ValidAuth1(material, otherFingerprint)),
        })
        {
            var (error, written) = await HostAsync(bytes, material, fingerprint);
            Assert.IsType<AuthFailedException>(error);
            Assert.Equal("AUTH1 verification failed", error!.Message);   // رسالة واحدة: لا تفصح أي حقل فشل
            Assert.Empty(written);
            _output.WriteLine($"{label}: AuthFailedException, 0 bytes written");
        }
    }

    // ---------- إعادة الإرسال ----------

    /// <summary>
    /// ‏<b>خاصية موثَّقة لا عيبًا:</b> ‏AUTH1 صحيحة تُقبل مرة أخرى إن أُعيد إرسالها بحرفيتها. لا nonce للخادم في
    /// العقد (القسم 4)، فالحماية طبقتان: الرسالة لا تُرى أصلًا لأنها داخل TLS مثبَّت على بصمة المضيف، والمضيف
    /// لا يرسل AUTH2 إلا لأول اتصال يجتاز AUTH1 ويغلق ما عداه (<c>SymmetricConnector.TryClaim</c>).
    /// الاختبار يثبّت الخاصية حتى لا تتغير بصمت، وحتى يُتخذ قرار صريح إن أُريد تغييرها.
    /// </summary>
    [Fact]
    public async Task ReplayedAuth1_IsAcceptedByTheHandshakeItself_ByDesign()
    {
        var (material, fingerprint) = Fixture(8);
        var captured = ValidAuth1(material, fingerprint);

        var first = await HostAsync(captured, material, fingerprint);
        var replay = await HostAsync(captured, material, fingerprint);

        Assert.Null(first.Error);
        Assert.Null(replay.Error);
        Assert.Equal(first.Written, replay.Written); // نفس AUTH2 بالضبط: الرد دالة في المدخل وحده
        _output.WriteLine("AUTH1 replay is accepted by the handshake; TLS pinning and the host's single-claim rule are what bound it");
    }

    // ---------- التوقيت ----------

    /// <summary>
    /// <b>ما يُقاس ولماذا هكذا:</b> ما يراه المهاجم فعلًا هو الزمن من إرسال AUTH1 إلى إغلاق المقبس، فالقياس هنا
    /// من طرف إلى طرف على مسار الفشل كاملًا (بما فيه رمي الاستثناء) لا على المقارنة وحدها. تُقاس ثلاث مجموعات
    /// متداخلة:
    /// <list type="bullet">
    ///   <item><b>سر خاطئ</b> و<b>معرّف جلسة خاطئ</b>: كلاهما يجب أن يمر بالمسار نفسه — المقارنة على الحقلين معًا
    ///     بـ <c>&amp;</c> لا <c>&amp;&amp;</c>، والـ MAC يُحسب دائمًا.</item>
    ///   <item><b>إصدار خاطئ</b>: عودة مبكرة <i>مقصودة ومعلنة</i> في العقد، وتعمل هنا شاهدًا موجبًا يقيس ما يستطيع
    ///     هذا المقياس رؤيته أصلًا.</item>
    /// </list>
    /// الحكم نسبي إلى الضجيج لا إلى رقم مطلق: يُقارَن الفرق بين المجموعتين بتشتّت المجموعة الواحدة مع نفسها
    /// (نصفان متناوبان). لو عاد أحد المسارين مبكرًا لظهر فرق أكبر من ضجيجه هو.
    /// </summary>
    [Fact]
    public async Task WrongSecretAndWrongSessionId_AreNotDistinguishableByTiming()
    {
        var (material, fingerprint) = Fixture(9);
        var (other, _) = Fixture(10);
        var wrongSecret = ValidAuth1(new SessionMaterial(material.SessionId, TunnelRole.Host, other.Secret, material.ExpiresAt, true, "203.0.113.1"), fingerprint);
        var wrongSession = ValidAuth1(new SessionMaterial(other.SessionId, TunnelRole.Host, material.Secret, material.ExpiresAt, true, "203.0.113.1"), fingerprint);
        var wrongVersion = (byte[])wrongSecret.Clone();
        wrongVersion[0] = 0xEE;

        const int warmup = 300;
        const int samples = 1500;
        for (var i = 0; i < warmup; i++)
        {
            await HostAsync(wrongSecret, material, fingerprint);
            await HostAsync(wrongSession, material, fingerprint);
            await HostAsync(wrongVersion, material, fingerprint);
        }

        // نصفان لكل مجموعة بالتناوب: الفرق بين نصفَي المجموعة الواحدة هو أرضية الضجيج بعينها.
        var secretA = new List<double>(samples / 2);
        var secretB = new List<double>(samples / 2);
        var session = new List<double>(samples);
        var version = new List<double>(samples);
        for (var i = 0; i < samples; i++)
        {
            (i % 2 == 0 ? secretA : secretB).Add(await MeasureAsync(wrongSecret, material, fingerprint));
            session.Add(await MeasureAsync(wrongSession, material, fingerprint));
            version.Add(await MeasureAsync(wrongVersion, material, fingerprint));
        }

        var mSecretA = Median(secretA);
        var mSecretB = Median(secretB);
        var mSession = Median(session);
        var mVersion = Median(version);
        var noise = Math.Abs(mSecretA - mSecretB);                       // نفس المدخل مرتين: ضجيج خالص
        var signal = Math.Abs((mSecretA + mSecretB) / 2 - mSession);     // مدخلان يفترض أنهما على المسار نفسه
        var control = Math.Abs((mSecretA + mSecretB) / 2 - mVersion);    // عودة مبكرة معلومة

        _output.WriteLine(
            $"median end-to-end failure time: wrong secret {mSecretA * 1000:F2}/{mSecretB * 1000:F2} µs, " +
            $"wrong session id {mSession * 1000:F2} µs, wrong version (known early return) {mVersion * 1000:F2} µs");
        _output.WriteLine(
            $"noise floor (same input, split halves) {noise * 1000:F2} µs; secret-vs-session difference {signal * 1000:F2} µs; " +
            $"early-return control {control * 1000:F2} µs");

        Assert.True(signal <= Math.Max(3 * noise, 0.002),
            $"wrong-secret and wrong-session-id differ by {signal * 1000:F2} µs against a {noise * 1000:F2} µs noise floor; one path is returning early");
    }

    private static double Median(List<double> values)
    {
        var sorted = values.ToList();
        sorted.Sort();
        return sorted[sorted.Count / 2];
    }

    private static async Task<double> MeasureAsync(byte[] input, SessionMaterial material, byte[] fingerprint)
    {
        var clock = Stopwatch.StartNew();
        await HostAsync(input, material, fingerprint);
        return clock.Elapsed.TotalMilliseconds;
    }

    /// <summary>
    /// الأنواع المسموح بها عند الفشل. أي نوع آخر (‏<c>IndexOutOfRange</c>، <c>NullReference</c>، …) يعني أن مدخلًا
    /// معاديًا وصل إلى مسار لم يُصمَّم له.
    /// </summary>
    private static void AssertExpectedAuthFailure(Exception error, string what)
        => Assert.True(error is AuthFailedException or TimeoutException or OperationCanceledException,
            $"{what} threw {error.GetType().FullName}: {error.Message}");
}
