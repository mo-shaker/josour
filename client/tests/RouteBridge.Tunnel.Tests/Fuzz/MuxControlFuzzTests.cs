using RouteBridge.Core.Tunnel;
using RouteBridge.Tunnel.Mux;
using RouteBridge.Tunnel.Tests.Mux;
using Xunit.Abstractions;

namespace RouteBridge.Tunnel.Tests.Fuzz;

/// <summary>
/// ‏fuzz على <b>ما نملكه داخل إطارات Nerdbank</b>: قناة التحكم المزروعة (PING/PONG/GOAWAY بتسعة بايتات)،
/// وبروتوكول بايت الحالة عند الفتح، واسم القناة <c>host:port</c>. الطرف المعادي هنا
/// <see cref="RawMuxPeer"/>: يتكلم بروتوكول Nerdbank 3 بصحة تامة ويكذب فوقه.
/// </summary>
public class MuxControlFuzzTests
{
    private const byte CtlPing = 1;
    private const byte CtlPong = 2;
    private const byte CtlGoAway = 3;
    private static readonly TimeSpan Settle = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan Short = TimeSpan.FromSeconds(10);

    private readonly ITestOutputHelper _output;

    public MuxControlFuzzTests(ITestOutputHelper output) => _output = output;

    // ---------- أنواع إطارات غير معروفة ----------

    /// <summary>
    /// نوع إطار خارج {1,2,3}: العقد يقول <c>GOAWAY(protocol_error)</c> ثم إغلاق. والأهم أن الإغلاق يظهر
    /// <b>عطلًا</b>: الإغلاق النظيف لا يرفع <c>Died</c> في <c>TunnelSession</c>، فكان الطرف المعادي ينهي الجلسة
    /// بلا سبب ويبلَّغ الخادم بإنهاء عادي.
    /// </summary>
    /// <remarks>
    /// الحالات متوازية لا متعاقبة: كل واحدة تدفع نصف ثانية من تصريف GOAWAY (سلوك صحيح — ننتظر وصول الإطار قبل
    /// إسقاط النقل)، والتعاقب يجعل سبع حالات ثلاث ثوانٍ ونصفًا في المجموعة الافتراضية بلا مقابل.
    /// </remarks>
    [Fact]
    public async Task UnknownControlFrameType_SendsGoAwayProtocolError_AndFaultsCompletion()
    {
        await Task.WhenAll(new byte[] { 0, 4, 0x7f, 0xff }.Select(async type =>
        {
            await using var peer = await RawMuxPeer.CreateAsync();
            await peer.WriteControlFrameAsync(type, new byte[8]);

            var verdict = await peer.SettleAsync(Settle);
            Assert.True(verdict.Completed, $"type 0x{type:x2}: {verdict.Describe()}");
            Assert.True(verdict.Faulted, $"an unknown control frame (0x{type:x2}) closed the tunnel cleanly: {verdict.Describe()}");
            Assert.IsType<MuxClosedException>(verdict.Error);
            Assert.Equal(GoAwayReason.ProtocolError, peer.Victim.RemoteGoAway);
            Assert.True(await peer.WaitForGoAwayAsync(Short), $"type 0x{type:x2}: no GOAWAY reached the peer");
            Assert.Equal((byte)GoAwayReason.ProtocolError, peer.LastGoAwayReason);
        }));
    }

    /// <summary>سبب GOAWAY خارج العقد (1..3): يُعامل خطأ بروتوكول لا إغلاقًا نظيفًا بسبب مجهول.</summary>
    [Fact]
    public async Task GoAwayWithUnknownReason_IsAProtocolError()
    {
        await Task.WhenAll(new byte[] { 0, 4, 0xff }.Select(async reason =>
        {
            await using var peer = await RawMuxPeer.CreateAsync();
            var payload = new byte[8];
            payload[0] = reason;
            await peer.WriteControlFrameAsync(CtlGoAway, payload);

            var verdict = await peer.SettleAsync(Settle);
            Assert.True(verdict.Completed, $"reason {reason}: {verdict.Describe()}");
            Assert.True(verdict.Faulted, $"reason {reason}: {verdict.Describe()}");
            Assert.IsType<MuxClosedException>(verdict.Error);
            Assert.Equal(GoAwayReason.ProtocolError, peer.Victim.RemoteGoAway);
        }));
    }

    /// <summary>‏<c>GOAWAY(protocol_error)</c> من الطرف الآخر: نهاية غير طبيعية يجب أن يعرفها التطبيق.</summary>
    [Fact]
    public async Task PeerGoAwayProtocolError_FaultsCompletion()
    {
        await using var peer = await RawMuxPeer.CreateAsync();
        var payload = new byte[8];
        payload[0] = (byte)GoAwayReason.ProtocolError;
        await peer.WriteControlFrameAsync(CtlGoAway, payload);

        var verdict = await peer.SettleAsync(Settle);
        Assert.True(verdict.Faulted, verdict.Describe());
        Assert.IsType<MuxClosedException>(verdict.Error);
        Assert.Equal(GoAwayReason.ProtocolError, peer.Victim.RemoteGoAway);
    }

    /// <summary>‏<c>GOAWAY(session_end)</c> و<c>(expired)</c> يبقيان إغلاقًا نظيفًا: نهاية مقصودة لا موت.</summary>
    [Theory]
    [InlineData(GoAwayReason.SessionEnd)]
    [InlineData(GoAwayReason.Expired)]
    public async Task PeerGoAwayWithContractReason_StaysAClean_Close(GoAwayReason reason)
    {
        await using var peer = await RawMuxPeer.CreateAsync();
        var payload = new byte[8];
        payload[0] = (byte)reason;
        await peer.WriteControlFrameAsync(CtlGoAway, payload);

        var verdict = await peer.SettleAsync(Settle);
        Assert.True(verdict.Completed, verdict.Describe());
        Assert.False(verdict.Faulted, verdict.Describe());
        Assert.Equal(reason, peer.Victim.RemoteGoAway);
    }

    // ---------- إطارات ناقصة ومقسومة ----------

    /// <summary>‏1..8 بايت ثم صمت: لا تعليق، ولا استهلاك خاطئ، والإطار الذي يكتمل بعدها يُعالَج صحيحًا.</summary>
    [Fact]
    public async Task PartialControlFrame_IsHeldUntilItCompletes()
    {
        await Task.WhenAll(Enumerable.Range(1, 8).Select(async prefix =>
        {
            await using var peer = await RawMuxPeer.CreateAsync();
            var frame = new byte[9];
            frame[0] = CtlPing;
            frame[1] = 0xAB;

            await peer.WriteControlAsync(frame.AsMemory(0, prefix));
            await Task.Delay(200);
            Assert.False(peer.Victim.IsClosed, $"a {prefix}-byte partial control frame closed the tunnel by itself");
            Assert.Equal(0, peer.PongsReceived);

            await peer.WriteControlAsync(frame.AsMemory(prefix));
            Assert.True(await peer.WaitForPongsAsync(1, Short), $"prefix {prefix}: the completed PING was never answered");
            Assert.False(peer.Victim.IsClosed);
        }));
    }

    /// <summary>إطارات مقسومة على كتابات ببايت واحد: لا فرق في النتيجة، وهذا ما يفعله السلك الحقيقي تحت الازدحام.</summary>
    [Fact]
    public async Task ControlFrames_SplitAcrossSingleByteWrites_AreStillParsed()
    {
        await using var peer = await RawMuxPeer.CreateAsync();
        const int pings = 20;
        var payload = new byte[pings * 9];
        for (var i = 0; i < pings; i++)
        {
            payload[i * 9] = CtlPing;
            payload[i * 9 + 1] = (byte)i;
        }
        for (var i = 0; i < payload.Length; i++) await peer.WriteControlAsync(payload.AsMemory(i, 1));

        Assert.True(await peer.WaitForPongsAsync(pings, TimeSpan.FromSeconds(20)), $"only {peer.PongsReceived} of {pings} PONGs came back");
        Assert.False(peer.Victim.IsClosed);
    }

    // ---------- حمولات معادية على قناة سليمة ----------

    /// <summary>‏PONG بنونس لم نطلبه: يُهمل، ولا يضيف شيئًا إلى الخريطة، ولا يقتل النفق.</summary>
    [Fact]
    public async Task UnsolicitedPongs_AreIgnored_AndDoNotGrowThePendingMap()
    {
        await using var peer = await RawMuxPeer.CreateAsync();
        peer.AnswerPings = true;
        for (var i = 0; i < 500; i++)
        {
            var payload = new byte[8];
            BitConverter.TryWriteBytes(payload, (ulong)i);
            await peer.WriteControlFrameAsync(CtlPong, payload);
        }
        await Task.Delay(300);

        Assert.False(peer.Victim.IsClosed, "unsolicited PONGs must not close the tunnel");
        Assert.Equal(0, peer.Victim.PendingPings);
        // النفق ما زال صالحًا: PING منا يعود بـ PONG.
        var rtt = await peer.Victim.PingAsync(CancellationToken.None).WaitAsync(Short);
        Assert.InRange(rtt.TotalMilliseconds, 0, 10_000);
    }

    /// <summary>
    /// طوفان PING: يجب أن يُجاب كله بلا نموّ ذاكرة بلا حد. الضغط العكسي هنا حقيقي (نافذة قناة التحكم 4 MiB)،
    /// فالغرض إثبات أن الرد محكوم بالنافذة لا أن الطابور يكبر في ذاكرتنا.
    /// </summary>
    [Fact]
    public async Task PingFlood_IsAnswered_WithoutUnboundedMemory()
    {
        await using var peer = await RawMuxPeer.CreateAsync();
        const int pings = 20_000;
        var before = GC.GetTotalMemory(forceFullCollection: true);

        var payload = new byte[pings * 9];
        for (var i = 0; i < pings; i++)
        {
            payload[i * 9] = CtlPing;
            BitConverter.TryWriteBytes(payload.AsSpan(i * 9 + 1, 8), (ulong)i);
        }
        await peer.WriteControlAsync(payload);

        Assert.True(await peer.WaitForPongsAsync(pings, TimeSpan.FromSeconds(60)), $"only {peer.PongsReceived} of {pings} PONGs came back");
        var after = GC.GetTotalMemory(forceFullCollection: true);
        _output.WriteLine($"ping flood: {pings} PINGs answered; managed heap {before / 1024} KiB → {after / 1024} KiB");

        Assert.False(peer.Victim.IsClosed);
        Assert.Equal(0, peer.Victim.PendingPings);
        Assert.True(after - before < 64L * 1024 * 1024, $"the ping flood grew the heap by {(after - before) / 1024 / 1024} MiB");
    }

    /// <summary>بايتات عشوائية على قناة التحكم: دائمًا حالة نهائية معرَّفة، وبلا استثناء من نوع آخر.</summary>
    [Fact]
    public Task RandomControlBytes_AlwaysEndInADefinedState() => RandomControlBytesAsync(FuzzSeed.Cases(32));

    [Trait("Category", "Benchmark")]
    [Fact]
    public Task Deep_RandomControlBytes() => RandomControlBytesAsync(FuzzSeed.Cases(600));

    /// <summary>
    /// الحالات مستقلة تمامًا فتُشغَّل على دفعات متوازية: كل حالة تدفع نصف ثانية من تصريف GOAWAY (السلوك الصحيح:
    /// ننتظر أن يصل الإطار قبل إسقاط النقل)، والتسلسل يجعلها دقائق بلا فائدة.
    /// </summary>
    private async Task RandomControlBytesAsync(int cases)
    {
        const int lanes = 8;
        using var watch = new UnobservedExceptionWatch();
        var faulted = 0;
        var alive = 0;

        for (var start = 0; start < cases; start += lanes)
        {
            var batch = Enumerable.Range(start, Math.Min(lanes, cases - start)).Select(async i =>
            {
                var rng = FuzzSeed.For("control", i);
                await using var peer = await RawMuxPeer.CreateAsync();
                await peer.WriteControlAsync(Mutate.Random(rng, rng.Next(1, 512)));

                // ثلاث ثوانٍ لا عشر: المسار المنتهي يستقر خلال نصف ثانية (تصريف GOAWAY)، أما الحمولة التي تبقي
                // النفق حيًّا (أقل من تسعة بايتات، أو إطارات PING/PONG صالحة بالصدفة) فتنتظر المهلة كاملة —
                // وبعشر ثوانٍ كانت حالة واحدة من كل ستين تضيف عشر ثوانٍ إلى المجموعة الافتراضية.
                var verdict = await peer.SettleAsync(TimeSpan.FromSeconds(3));
                if (!verdict.Completed)
                {
                    // أقل من تسعة بايتات، أو بايتات كلها PING/PONG صالحة: النفق حيّ وهذا مقبول.
                    Assert.False(peer.Victim.IsClosed, $"case {i}: mux is closed but Completion never settled");
                    Interlocked.Increment(ref alive);
                    return;
                }
                Assert.True(verdict.Faulted, $"case {i}: random control bytes produced a clean close: {verdict.Describe()}");
                Assert.IsType<MuxClosedException>(verdict.Error);
                Interlocked.Increment(ref faulted);
            });
            await Task.WhenAll(batch);
        }

        var unobserved = watch.Drain();
        var ours = unobserved.Where(UnobservedExceptionWatch.IsOurs).ToList();
        _output.WriteLine($"random control bytes: {cases} cases, {faulted} faulted, {alive} still alive; unobserved: {ours.Count} ours, {unobserved.Count - ours.Count} from Nerdbank.Streams");
        // ما نصنعه نحن يجب أن يكون صفرًا؛ ضجيج مهام Nerdbank الداخلية موثَّق ولا نملك إصلاحه.
        Assert.True(ours.Count == 0, UnobservedExceptionWatch.Describe(ours));
    }

    // ---------- بروتوكول الفتح: الاسم وبايت الحالة ----------

    /// <summary>
    /// أسماء قنوات معادية: العقد يقول <c>host:port</c> بلا IPv6 حرفي. كل ما لا يطابق يجب أن يعود
    /// <c>OPEN_FAIL(not_allowed)</c>، لا أن يصل إلى سياسة الخروج ولا أن يعلّق العارض.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData(":")]
    [InlineData(":443")]
    [InlineData("host:")]
    [InlineData("host:0")]
    [InlineData("host:65536")]
    [InlineData("host:-1")]
    [InlineData("host:+443")]
    [InlineData("host: 443")]
    [InlineData("host:443 ")]
    [InlineData("a:b:443")]
    [InlineData("[::1]:443")]
    [InlineData("host:99999999999999999999")]
    [InlineData("host:4e2")]
    [InlineData("host:0x1bb")]
    public async Task AdversarialChannelNames_AreRejectedBeforeThePolicy(string name)
    {
        await using var peer = await RawMuxPeer.CreateAsync();
        var reached = 0;
        peer.Victim.OpenRequested = (_, _) =>
        {
            Interlocked.Increment(ref reached);
            return Task.FromResult(MuxOpenDecision.Ok(new TestTarget()));
        };

        var status = await peer.OfferAndReadStatusAsync(name, Short);

        Assert.Equal((byte)OpenFailReason.NotAllowed, status);
        Assert.Equal(0, reached);
        Assert.False(peer.Victim.IsClosed);
    }

    /// <summary>
    /// أسماء قنوات ضخمة: الحد 300 محرف في <c>TryParseName</c> يمنع تمرير اسم بلا سقف إلى سياسة الخروج أو إلى
    /// مجموعة النطاقات. الأسماء الأكبر من إطار Nerdbank لا تخرج من الطرف المعادي أصلًا (‏<c>null</c> هنا)،
    /// وهو حد إضافي مجاني لكنه ليس حدَّنا: المطلوب ألا يصل شيء من هذا إلى ما بعد المحلل.
    /// </summary>
    [Theory]
    [InlineData(301)]
    [InlineData(4096)]
    [InlineData(64 * 1024)]
    public async Task HugeChannelName_NeverReachesThePolicy(int length)
    {
        await using var peer = await RawMuxPeer.CreateAsync();
        var reached = 0;
        peer.Victim.OpenRequested = (_, _) => { Interlocked.Increment(ref reached); return Task.FromResult(MuxOpenDecision.Fail(OpenFailReason.NotAllowed)); };

        var status = await peer.OfferAndReadStatusAsync(new string('x', length) + ":443", TimeSpan.FromSeconds(20));

        _output.WriteLine($"name of {length} chars → status {(status is null ? "none (the peer could not even offer it)" : status.ToString())}");
        Assert.True(status is null or (byte)OpenFailReason.NotAllowed, $"a {length}-char name produced status {status}");
        Assert.Equal(0, reached);
    }

    /// <summary>
    /// طوفان عروض فوق حد الشريحة: الرفض <c>OPEN_FAIL(limit)</c>، والعدّاد يعود إلى الصفر، والذاكرة محكومة.
    /// بلا حد على مسار <b>القبول</b> يستطيع الطرف الآخر وحده أن يقرر كم قناة نُنشئ.
    /// </summary>
    [Fact]
    public async Task OfferFloodBeyondTheStreamLimit_IsRejectedAsLimit()
    {
        const int limit = 8;
        await using var peer = await RawMuxPeer.CreateAsync(
            victimOptions: new MuxOptions { EnableLiveness = false, ReceiveWindow = MuxWindow.NearWindow, MaxConcurrentStreams = limit });

        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        peer.Victim.OpenRequested = async (_, ct) => { await hold.Task.WaitAsync(ct); return MuxOpenDecision.Fail(OpenFailReason.NotAllowed); };

        var before = GC.GetTotalMemory(forceFullCollection: true);
        var offers = Enumerable.Range(0, 64).Select(i => peer.OfferAndReadStatusAsync($"h{i}.test:443", TimeSpan.FromSeconds(20))).ToArray();
        // البوابة تُفتح والطوفان ما زال جاريًا: المقبولون محتجزون حتى يتجاوز العرض الحد، ثم يُطلَقون.
        var release = Task.Run(async () => { await Task.Delay(1500); hold.TrySetResult(); });
        var statuses = await Task.WhenAll(offers);
        await release;
        var after = GC.GetTotalMemory(forceFullCollection: true);

        var limited = statuses.Count(s => s == (byte)OpenFailReason.Limit);
        _output.WriteLine($"offer flood: {limited} of 64 offers rejected as limit (concurrent cap {limit}); heap {before / 1024} KiB → {after / 1024} KiB");
        Assert.True(limited >= 64 - limit, $"only {limited} of 64 offers were capped; the accept path has no limit of its own");
        Assert.True(after - before < 64L * 1024 * 1024, $"the offer flood grew the heap by {(after - before) / 1024 / 1024} MiB");

        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (peer.Victim.Stats.OpenStreams > 0 && DateTime.UtcNow < deadline) await Task.Delay(20);
        Assert.Equal(0, peer.Victim.Stats.OpenStreams);
    }

    /// <summary>بايت حالة خارج 0..7 من مضيف معادٍ: يُطبَّع إلى سبب من العقد، ولا يظهر stream مفتوح.</summary>
    [Theory]
    [InlineData((byte)8)]
    [InlineData((byte)9)]
    [InlineData((byte)0x7f)]
    [InlineData((byte)0xff)]
    public async Task UnknownOpenStatusByte_IsNormalisedToAContractReason(byte status)
    {
        await using var peer = await RawMuxPeer.CreateAsync(victimRole: TunnelRole.Guest);
        peer.AcceptOffersWith(_ => status);

        var open = await peer.Victim.OpenStreamAsync("example.com", 443, CancellationToken.None).WaitAsync(Short);

        Assert.False(open.IsOpen);
        Assert.NotNull(open.Reason);
        Assert.True(MuxWire.IsValid(open.Reason!.Value), $"status 0x{status:x2} produced reason {(int)open.Reason!.Value}, which is not in the contract");
        Assert.Equal(0, peer.Victim.Stats.OpenStreams);
    }

    /// <summary>مضيف معادٍ يقبل القناة ثم يغلقها بلا بايت حالة: فشل معرَّف لا تعليق ولا تسريب حجز.</summary>
    [Fact]
    public async Task ChannelClosedBeforeTheStatusByte_FailsCleanly()
    {
        await using var peer = await RawMuxPeer.CreateAsync(victimRole: TunnelRole.Guest);
        peer.AcceptOffersWith(_ => null);

        var open = await peer.Victim.OpenStreamAsync("example.com", 443, CancellationToken.None).WaitAsync(Short);

        Assert.False(open.IsOpen);
        Assert.Equal(OpenFailReason.ConnectFailed, open.Reason);
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (peer.Victim.Stats.OpenStreams > 0 && DateTime.UtcNow < deadline) await Task.Delay(20);
        Assert.Equal(0, peer.Victim.Stats.OpenStreams);
    }

    /// <summary>‏fuzz على بايت الحالة: أي قيمة من 0 إلى 255 تنتهي إما بـ stream مفتوح وإما بسبب من العقد.</summary>
    [Fact]
    public async Task StatusByteFuzz_CoversTheWholeByteRange()
    {
        var opened = 0;
        var failed = 0;
        for (var value = 0; value < 256; value++)
        {
            await using var peer = await RawMuxPeer.CreateAsync(victimRole: TunnelRole.Guest);
            peer.AcceptOffersWith(_ => (byte)value);
            var open = await peer.Victim.OpenStreamAsync("example.com", 443, CancellationToken.None).WaitAsync(Short);
            if (open.IsOpen)
            {
                Assert.Equal(0, value); // 0 = OPEN_OK وحده
                opened++;
                await open.Stream!.DisposeAsync();
            }
            else
            {
                Assert.True(MuxWire.IsValid(open.Reason!.Value), $"status {value} produced reason {(int)open.Reason!.Value}");
                failed++;
            }
        }
        _output.WriteLine($"status byte fuzz: {opened} opened, {failed} rejected with a contract reason");
        Assert.Equal(1, opened);
        Assert.Equal(255, failed);
    }

    // ---------- GOAWAY في منتصف حركة ----------

    /// <summary>‏GOAWAY يصل بينما البيانات تتحرك على stream: إغلاق معرَّف، والـ stream يعطي خطأ IO لا نوعًا مفاجئًا.</summary>
    [Fact]
    public async Task GoAwayMidStream_ClosesEverythingWithAKnownExceptionType()
    {
        await using var peer = await RawMuxPeer.CreateAsync(victimRole: TunnelRole.Guest);
        peer.AcceptOffersWith(_ => (byte)0);

        var open = await peer.Victim.OpenStreamAsync("example.com", 443, CancellationToken.None).WaitAsync(Short);
        Assert.True(open.IsOpen);
        var stream = open.Stream!;

        var writer = Task.Run(async () =>
        {
            var chunk = new byte[16 * 1024];
            try
            {
                for (var i = 0; i < 4096; i++) await stream.WriteAsync(chunk);
                return (Exception?)null;
            }
            catch (Exception e) { return e; }
        });

        await Task.Delay(100);
        var payload = new byte[8];
        payload[0] = (byte)GoAwayReason.SessionEnd;
        await peer.WriteControlFrameAsync(CtlGoAway, payload);

        var verdict = await peer.SettleAsync(Settle);
        Assert.True(verdict.Completed, verdict.Describe());
        Assert.Equal(GoAwayReason.SessionEnd, peer.Victim.RemoteGoAway);

        var error = await writer.WaitAsync(Settle);
        // الكاتب إما أنهى ما عليه قبل الإغلاق وإما رأى IOException/ObjectDisposedException؛ لا نوع خارج هذين.
        Assert.True(error is null or IOException or ObjectDisposedException or OperationCanceledException,
            $"writing during GOAWAY threw {error?.GetType().FullName}: {error?.Message}");
        await stream.DisposeAsync();
    }
}
