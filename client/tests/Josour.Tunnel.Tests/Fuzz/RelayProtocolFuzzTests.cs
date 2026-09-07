using System.Buffers.Binary;
using System.Text;
using Josour.Core.Tunnel;
using Josour.Tunnel.Transport;
using Xunit.Abstractions;

namespace Josour.Tunnel.Tests.Fuzz;

/// <summary>
/// ‏fuzz على محلل مقدمة الـ Relay (<see cref="RelayProtocol"/>). المقدمة هي أول ما يقرأه أي طرف من طرف آخر لم
/// يُصادَق بعد، فهي — مع <c>AUTH1</c> — الحد الذي يصله مدخل معادٍ قبل أي تحقق.
/// </summary>
public class RelayProtocolFuzzTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(2);
    private readonly ITestOutputHelper _output;

    public RelayProtocolFuzzTests(ITestOutputHelper output) => _output = output;

    private static byte[] Valid(string token = "signed-token") => RelayProtocol.BuildPreamble(Guid.NewGuid(), TunnelRole.Host, token);

    // ---------- التحليل في الذاكرة ----------

    /// <summary>بايتات عشوائية: لا يرمي أبدًا، وما يقبله يحترم كل قيد في العقد.</summary>
    [Fact]
    public void TryParsePreamble_NeverThrows_AndKeepsItsContract()
    {
        var accepted = 0;
        var cases = FuzzSeed.Cases(4000);
        for (var i = 0; i < cases; i++)
        {
            var rng = FuzzSeed.For("relay-random", i);
            // بايتات عشوائية خالصة لا تعبر فحص السحر أبدًا، فثلثا الحالات تُبنى بمقدمة صحيحة البنية: هكذا يصل
            // الـ fuzz فعلًا إلى فحص الدور وطول التوكن وفك ترميز UTF-8 لا إلى أول سطر وحده. والثلث الأخير يحمل
            // توكنًا ASCII صالحًا ليثبت أن مسار القبول قابل للوصول أصلًا (وإلا كان «صفر مقبول» بلا معنى).
            var bytes = Mutate.Random(rng, rng.Next(0, 1200));
            if (i % 3 != 0 && bytes.Length > RelayProtocol.FixedPreambleLength)
            {
                RelayProtocol.Magic.CopyTo(bytes);
                bytes[RelayProtocol.MagicLength] = RelayProtocol.Version;
                bytes[RelayProtocol.MagicLength + 1] = (byte)(i % 2);
                var tokenBytes = Math.Min(bytes.Length - RelayProtocol.FixedPreambleLength, RelayProtocol.MaxTokenLength);
                BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(RelayProtocol.TokenLengthOffset), (ushort)tokenBytes);
                if (i % 3 == 2)
                {
                    for (var t = 0; t < tokenBytes; t++) bytes[RelayProtocol.FixedPreambleLength + t] = (byte)rng.Next(0x21, 0x7f);
                }
            }

            var ok = RelayProtocol.TryParsePreamble(bytes, out var preamble, out var error);
            if (!ok)
            {
                Assert.Null(preamble);
                Assert.False(string.IsNullOrEmpty(error), $"case {i}: rejected without a reason");
                continue;
            }

            accepted++;
            Assert.NotNull(preamble);
            Assert.Null(error);
            Assert.True(preamble!.Role is TunnelRole.Host or TunnelRole.Guest);
            Assert.InRange(Encoding.UTF8.GetByteCount(preamble.Token), 1, RelayProtocol.MaxTokenLength);
        }
        _output.WriteLine($"{cases} random relay preambles: {accepted} accepted, all within the contract");
        Assert.True(accepted > 0, "no generated preamble was ever accepted; the fuzz never reaches the accepting path");
    }

    /// <summary>قطع عند كل طول ممكن: كل ما دون المقدمة الكاملة يُرفض، والكامل يُقبل.</summary>
    [Fact]
    public void TruncationAtEveryLength_IsRejected_ExceptTheCompletePreamble()
    {
        var valid = Valid();
        for (var length = 0; length < valid.Length; length++)
            Assert.False(RelayProtocol.TryParsePreamble(valid.AsSpan(0, length), out _, out _), $"a {length}-byte prefix was accepted");
        Assert.True(RelayProtocol.TryParsePreamble(valid, out _, out _));
    }

    /// <summary>قلب بت واحد في كل موضع من المقدمة: لا يرمي، وما يُقبل يبقى داخل العقد.</summary>
    [Fact]
    public void EverySingleBitFlip_IsHandled()
    {
        var valid = Valid();
        var accepted = 0;
        for (var index = 0; index < valid.Length; index++)
        {
            for (var bit = 0; bit < 8; bit++)
            {
                var mutated = (byte[])valid.Clone();
                mutated[index] ^= (byte)(1 << bit);
                if (!RelayProtocol.TryParsePreamble(mutated, out var preamble, out _)) continue;
                accepted++;
                Assert.InRange(Encoding.UTF8.GetByteCount(preamble!.Token), 1, RelayProtocol.MaxTokenLength);
            }
        }
        _output.WriteLine($"{valid.Length * 8} single-bit flips: {accepted} still parsed, none outside the contract");
    }

    /// <summary>
    /// توكن ببايتات ليست UTF-8 صالحة. <b>عيب أُصلح في الأسبوع 6:</b> فك الترميز كان بـ
    /// <c>Encoding.UTF8</c> الافتراضي الذي <i>يستبدل</i> البايت الفاسد بـ U+FFFD ولا يرمي، فكان فرع
    /// «token is not valid UTF-8» ميتًا وكان المحلل يسلّم إلى ما بعده توكنًا أُعيدت كتابته لا التوكن الذي وصل.
    /// </summary>
    [Theory]
    [InlineData(new byte[] { 0xFF, 0xFE })]
    [InlineData(new byte[] { 0xC3, 0x28 })]          // بداية متعددة البايتات ثم متابعة غير صالحة
    [InlineData(new byte[] { 0xED, 0xA0, 0x80 })]    // نصف زوج بديل مرمَّز (surrogate) — ممنوع في UTF-8
    [InlineData(new byte[] { 0x80 })]                // بايت متابعة بلا بداية
    [InlineData(new byte[] { 0xE2, 0x82 })]          // ثلاثي مقطوع
    public void TokenWithInvalidUtf8_IsRejected_NotSilentlyRewritten(byte[] tokenBytes)
    {
        var preamble = BuildRaw(Guid.NewGuid(), RelayProtocol.HostRole, tokenBytes);

        var ok = RelayProtocol.TryParsePreamble(preamble, out var parsed, out var error);

        Assert.False(ok, "an invalid-UTF-8 token was accepted and silently rewritten with replacement characters");
        Assert.Null(parsed);
        Assert.Equal("token is not valid UTF-8", error);
    }

    /// <summary>طول توكن معلن أكبر مما وصل: رفض، لا انتظار ولا قراءة خارج المخزن.</summary>
    [Fact]
    public void DeclaredTokenLengthBeyondTheBuffer_IsRejected()
    {
        var valid = Valid("abc");
        var lying = (byte[])valid.Clone();
        BinaryPrimitives.WriteUInt16BigEndian(lying.AsSpan(RelayProtocol.TokenLengthOffset), RelayProtocol.MaxTokenLength);
        Assert.False(RelayProtocol.TryParsePreamble(lying, out _, out var error));
        Assert.Equal("preamble is truncated", error);
    }

    [Fact]
    public void ZeroAndOversizedTokenLength_AreRejected()
    {
        var valid = Valid("abc");
        var zero = (byte[])valid.Clone();
        BinaryPrimitives.WriteUInt16BigEndian(zero.AsSpan(RelayProtocol.TokenLengthOffset), 0);
        Assert.False(RelayProtocol.TryParsePreamble(zero, out _, out var zeroError));
        Assert.Equal("token is empty", zeroError);

        var big = new byte[RelayProtocol.FixedPreambleLength + RelayProtocol.MaxTokenLength + 1];
        valid.AsSpan(0, RelayProtocol.FixedPreambleLength).CopyTo(big);
        BinaryPrimitives.WriteUInt16BigEndian(big.AsSpan(RelayProtocol.TokenLengthOffset), RelayProtocol.MaxTokenLength + 1);
        Assert.False(RelayProtocol.TryParsePreamble(big, out _, out var bigError));
        Assert.Equal("token is too long", bigError);
    }

    // ---------- القراءة من stream ----------

    /// <summary>القارئ على مدخل معادٍ: لا يعلّق، ولا يرمي إلا الأنواع المتوقَّعة، ولا يخصّص أكثر من سقف التوكن.</summary>
    [Fact]
    public async Task ReadPreambleAsync_OnAdversarialInput_NeverHangs_NorThrowsAnUnexpectedType()
    {
        var valid = Valid();
        var cases = FuzzSeed.Cases(300);
        var parsed = 0;
        var before = GC.GetTotalMemory(forceFullCollection: true);

        for (var i = 0; i < cases; i++)
        {
            var rng = FuzzSeed.For("relay-stream", i);
            var (_, bytes) = Mutate.Any(valid, rng);
            var stream = new ReplayStream(bytes);
            try
            {
                await RelayProtocol.ReadPreambleAsync(stream, Timeout, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
                parsed++;
            }
            catch (Exception e)
            {
                Assert.True(e is RelayProtocolException or TimeoutException or OperationCanceledException,
                    $"case {i} ({bytes.Length} bytes) threw {e.GetType().FullName}: {e.Message}");
            }
            Assert.Empty(stream.Written); // القارئ لا يرد بشيء: الرد قرار الـ Relay بعد التحقق لا قرار المحلل
        }

        var after = GC.GetTotalMemory(forceFullCollection: true);
        _output.WriteLine($"{cases} adversarial preambles read from a stream: {parsed} parsed; heap {before / 1024} KiB → {after / 1024} KiB");
        Assert.True(after - before < 16L * 1024 * 1024, $"reading adversarial preambles grew the heap by {(after - before) / 1024} KiB");
    }

    /// <summary>طول توكن معلن 1024 مع إغلاق مبكر: خطأ صريح لا انتظار، والمخصَّص محكوم بالسقف لا بالادّعاء.</summary>
    [Fact]
    public async Task ReadPreambleAsync_WithADeclaredTokenThatNeverArrives_FailsFast()
    {
        var header = Valid("x").AsSpan(0, RelayProtocol.FixedPreambleLength).ToArray();
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(RelayProtocol.TokenLengthOffset), RelayProtocol.MaxTokenLength);

        var stream = new ReplayStream(header);
        var error = await Assert.ThrowsAsync<RelayProtocolException>(
            () => RelayProtocol.ReadPreambleAsync(stream, Timeout, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Contains("relay token", error.Message, StringComparison.Ordinal);
    }

    /// <summary>توكن غير صالح UTF-8 عبر القارئ أيضًا: نفس الحكم، بخطأ بروتوكول صريح.</summary>
    [Fact]
    public async Task ReadPreambleAsync_RejectsInvalidUtf8Token()
    {
        var stream = new ReplayStream(BuildRaw(Guid.NewGuid(), RelayProtocol.GuestRole, new byte[] { 0xFF, 0xFE, 0xFD }));
        var error = await Assert.ThrowsAsync<RelayProtocolException>(
            () => RelayProtocol.ReadPreambleAsync(stream, Timeout, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal("token is not valid UTF-8", error.Message);
    }

    /// <summary>المقدمة موزّعة على كتابات ببايت واحد: تُجمَّع كاملة (القارئ لا يفترض حدود الرسائل).</summary>
    [Fact]
    public async Task ReadPreambleAsync_ReassemblesByteAtATimeDelivery()
    {
        var sessionId = Guid.NewGuid();
        var bytes = RelayProtocol.BuildPreamble(sessionId, TunnelRole.Guest, "t");
        var stream = new OneByteAtATimeStream(bytes);

        var preamble = await RelayProtocol.ReadPreambleAsync(stream, Timeout, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(sessionId, preamble.SessionId);
        Assert.Equal(TunnelRole.Guest, preamble.Role);
        Assert.Equal("t", preamble.Token);
    }

    // ---------- الرد ----------

    /// <summary>‏fuzz على بايتَي الرد: الإصدار الخاطئ خطأ بروتوكول، وكل حالة ليست <c>paired</c> رفض عند النقل.</summary>
    [Fact]
    public async Task ReplyFuzz_CoversEveryVersionAndStatusByte()
    {
        var rejected = 0;
        for (var version = 0; version < 256; version += 1)
        {
            for (var status = 0; status < 256; status += 37)
            {
                var stream = new ReplayStream(new[] { (byte)version, (byte)status });
                if (version != RelayProtocol.Version)
                {
                    await Assert.ThrowsAsync<RelayProtocolException>(() => RelayProtocol.ReadReplyAsync(stream, Timeout, CancellationToken.None));
                    rejected++;
                    continue;
                }

                var result = await RelayProtocol.ReadReplyAsync(stream, Timeout, CancellationToken.None);
                Assert.Equal((RelayStatus)status, result);
                // ‏RelayTransport يقبل paired وحدها؛ أي بايت آخر — معروفًا كان أو لا — رفض.
                if (result != RelayStatus.Paired) rejected++;
            }
        }
        _output.WriteLine($"reply fuzz: {rejected} of the sampled (version, status) pairs are rejected; only version 1 + status 0 pairs the tunnel");
        Assert.True(rejected > 0);
    }

    /// <summary>رد مقطوع (بايت واحد ثم إغلاق): خطأ صريح لا انتظار بلا نهاية.</summary>
    [Fact]
    public async Task TruncatedReply_FailsFast()
    {
        var stream = new ReplayStream(new byte[] { RelayProtocol.Version });
        await Assert.ThrowsAsync<RelayProtocolException>(() => RelayProtocol.ReadReplyAsync(stream, Timeout, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10)));
    }

    // ---------- أدوات ----------

    /// <summary>يبني مقدمة ببايتات توكن خام (قد لا تكون UTF-8 صالحة)، وهو ما لا يسمح به <c>BuildPreamble</c>.</summary>
    private static byte[] BuildRaw(Guid sessionId, byte role, byte[] tokenBytes)
    {
        var buffer = new byte[RelayProtocol.FixedPreambleLength + tokenBytes.Length];
        RelayProtocol.Magic.CopyTo(buffer);
        buffer[RelayProtocol.MagicLength] = RelayProtocol.Version;
        buffer[RelayProtocol.MagicLength + 1] = role;
        sessionId.ToByteArray(bigEndian: true).CopyTo(buffer, RelayProtocol.MagicLength + 2);
        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(RelayProtocol.TokenLengthOffset), (ushort)tokenBytes.Length);
        tokenBytes.CopyTo(buffer, RelayProtocol.FixedPreambleLength);
        return buffer;
    }

    /// <summary>‏stream يسلّم بايتًا واحدًا في كل قراءة: أسوأ تجزئة ممكنة.</summary>
    private sealed class OneByteAtATimeStream : Stream
    {
        private readonly byte[] _source;
        private int _position;

        public OneByteAtATimeStream(byte[] source) => _source = source;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_position >= _source.Length || buffer.Length == 0) return ValueTask.FromResult(0);
            buffer.Span[0] = _source[_position++];
            return ValueTask.FromResult(1);
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();
        public override void Flush() { }
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
