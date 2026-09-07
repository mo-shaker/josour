using Josour.Core.Tunnel;
using Josour.Tunnel.Mux;
using Josour.Tunnel.Tests.Mux;
using Xunit.Abstractions;

namespace Josour.Tunnel.Tests.Fuzz;

/// <summary>
/// ‏fuzz على <b>الـ stream المصادَق</b> الذي يُسلَّم إلى <see cref="NerdbankMux.Create"/> — أي على الحد الذي نملكه
/// بعد ADR-0006: الإطارات نفسها تملكها <c>Nerdbank.Streams</c>، لكن ما يحدث لعمليتنا حين تصلها إطارات مشوَّهة
/// مسؤوليتنا وحدنا. الحمولات مولَّدة من <see cref="MuxCorpus"/>: بايتات جلسة نفق حقيقية (فتح، بيانات، رفض،
/// PING، إغلاق نصفي، GOAWAY) ثم تُشوَّه.
///
/// الثوابت المؤكَّدة في كل حالة:
/// <list type="number">
///   <item>لا نوع استثناء غير متوقَّع: <see cref="NerdbankMux.Completion"/> إما ينتهي نظيفًا وإما يعطل بـ
///     <see cref="MuxClosedException"/> وحدها.</item>
///   <item>لا تعليق: تنتهي الحالة خلال مهلة صريحة، والتخلص من الـ Mux ينتهي أيضًا.</item>
///   <item>لا تخصيص بلا حد: الذاكرة المدارة بعد كل دفعة تبقى تحت سقف رغم أطوال معلنة بحجم 0xFFFFFFFF.</item>
///   <item>لا استثناء مهمة غير مُلاحَظ (التجميعة لا تتوازى، فما يُلتقط من عندنا).</item>
/// </list>
/// </summary>
public class MuxTransportFuzzTests
{
    private static readonly TimeSpan Settle = TimeSpan.FromSeconds(20);
    private readonly ITestOutputHelper _output;

    public MuxTransportFuzzTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task Corpus_IsRecordedFromARealSession()
    {
        var corpus = await MuxCorpus.GetAsync();
        _output.WriteLine($"corpus: {corpus.Length} bytes of real mux wire traffic");
        Assert.True(corpus.Length > 1024, $"corpus is only {corpus.Length} bytes; the recording script produced nothing to mutate");
    }

    [Fact]
    public Task MutatedWire_AlwaysEndsInADefinedState() => RunAsync("mutated", FuzzSeed.Cases(48), fragmented: false);

    [Fact]
    public Task MutatedWire_SplitAcrossTinyReads_AlwaysEndsInADefinedState() => RunAsync("fragmented", FuzzSeed.Cases(24), fragmented: true);

    [Trait("Category", "Benchmark")]
    [Fact]
    public Task Deep_MutatedWire() => RunAsync("deep", FuzzSeed.Cases(1500), fragmented: false);

    [Trait("Category", "Benchmark")]
    [Fact]
    public Task Deep_MutatedWire_Fragmented() => RunAsync("deep-fragmented", FuzzSeed.Cases(400), fragmented: true);

    private async Task RunAsync(string scope, int cases, bool fragmented)
    {
        var corpus = await MuxCorpus.GetAsync();
        using var watch = new UnobservedExceptionWatch();
        var faulted = 0;
        var clean = 0;
        var before = GC.GetTotalMemory(forceFullCollection: true);
        var peak = before;

        for (var i = 0; i < cases; i++)
        {
            var rng = FuzzSeed.For(scope, i);
            var (name, payload) = Mutate.Any(corpus, rng);
            var verdict = await OneCaseAsync(payload, rng, fragmented);

            Assert.True(verdict.Completed, $"case {i} ({name}, {payload.Length} B, seed scope '{scope}') hung: {verdict.Describe()}");
            if (verdict.Faulted)
            {
                Assert.True(verdict.Error is MuxClosedException,
                    $"case {i} ({name}, {payload.Length} B) faulted with {verdict.Error?.GetType().FullName}: {verdict.Error?.Message}");
                faulted++;
            }
            else
            {
                clean++;
            }

            if ((i & 7) == 7) peak = Math.Max(peak, GC.GetTotalMemory(forceFullCollection: true));
        }

        var after = GC.GetTotalMemory(forceFullCollection: true);
        var unobserved = watch.Drain();
        var ours = unobserved.Where(UnobservedExceptionWatch.IsOurs).ToList();
        _output.WriteLine($"[{scope}] {cases} cases: {faulted} faulted with MuxClosedException, {clean} closed cleanly; " +
            $"managed heap {before / 1024} KiB → {after / 1024} KiB (peak {peak / 1024} KiB); unobserved task exceptions: {ours.Count} ours, {unobserved.Count - ours.Count} from Nerdbank.Streams");

        // ما نصنعه نحن يجب أن يكون صفرًا؛ ضجيج مهام Nerdbank الداخلية موثَّق ولا نملك إصلاحه.
        Assert.True(ours.Count == 0, UnobservedExceptionWatch.Describe(ours));
        // سقف سخي جدًا: المهم أن أطوالًا معلنة بحجم 4 GiB لا تُخصَّص، لا أن نقيس الضجيج.
        Assert.True(peak - before < 192L * 1024 * 1024, $"managed heap grew by {(peak - before) / 1024 / 1024} MiB across {cases} fuzz cases");
    }

    private static async Task<MuxVerdict> OneCaseAsync(byte[] payload, Random rng, bool fragmented)
    {
        await using var victim = await MuxVictim.CreateAsync(TunnelRole.Host);
        // معالج مضيف حقيقي: أي قناة تنجو من التشويه تصل إلى مسار القرار لا إلى فراغ.
        victim.Mux.OpenRequested = (_, _) => Task.FromResult(MuxOpenDecision.Ok(new TestTarget(produce: 4096)));
        try
        {
            // التغذية على قطع 1..3 بايت مكلفة جدًا لكل بايت، فتُقتصر على مقدمة الحمولة: الغرض تقسيم الإطار لا حجمه.
            if (fragmented) await victim.FeedFragmentedAsync(payload.Length > 2048 ? payload[..2048] : payload, rng);
            else await victim.FeedAsync(payload);
        }
        catch (Exception)
        {
            // الضحية أغلقت السلك في منتصف الكتابة: ردّ مشروع على هراء، والحكم يأتي من Completion.
        }

        return await victim.CloseAndSettleAsync(Settle);
    }

    /// <summary>
    /// إطار يعلن طولًا خرافيًا: الفحص الصريح أن ما يُخصَّص محكوم بما وصل فعلًا لا بما ادّعاه الطرف الآخر.
    /// (‏<c>docs/protocol.md</c> القسم 5 يجعل تجاوز الطول <c>GOAWAY(protocol_error)</c>؛ فوق Nerdbank يُترجم
    /// ذلك إلى موت النقل، والمطلوب أن يكون موتًا لا نموًّا.)
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(64)]
    [InlineData(4096)]
    public async Task OversizedDeclaredLength_DoesNotAllocate(int prefixBytes)
    {
        var before = GC.GetTotalMemory(forceFullCollection: true);
        await using var victim = await MuxVictim.CreateAsync();
        var payload = new byte[prefixBytes + 16];
        payload.AsSpan().Fill(0xFF); // أي تفسير لأي حقل طول داخلها = أكبر قيمة ممكنة
        try { await victim.FeedAsync(payload); } catch (Exception) { /* أُغلق السلك */ }

        var verdict = await victim.CloseAndSettleAsync(Settle);
        var after = GC.GetTotalMemory(forceFullCollection: true);

        Assert.True(verdict.Completed, verdict.Describe());
        if (verdict.Faulted) Assert.IsType<MuxClosedException>(verdict.Error);
        Assert.True(after - before < 32L * 1024 * 1024, $"declared-length payload grew the heap by {(after - before) / 1024} KiB");
    }

    /// <summary>سلك يُقطع فورًا بلا بايت واحد: أبسط حالة، ويجب أن تكون عطلًا (نفق ميت) لا اكتمالًا صامتًا.</summary>
    [Fact]
    public async Task ImmediateEof_IsADeadTunnel_NotACleanClose()
    {
        await using var victim = await MuxVictim.CreateAsync();
        var verdict = await victim.CloseAndSettleAsync(Settle);
        Assert.True(verdict.Completed, verdict.Describe());
        Assert.True(verdict.Faulted, "EOF without GOAWAY must fault Completion, otherwise the app reports a normal end");
        Assert.IsType<MuxClosedException>(verdict.Error);
    }

    /// <summary>‏fuzz على محلل اسم القناة (<c>host:port</c>): لا يرمي أبدًا، وما يقبله يحترم العقد.</summary>
    [Fact]
    public void TryParseName_NeverThrows_AndKeepsItsContract()
    {
        var alphabet = "abcAZ09.:-_[]% é😀/ \t\\".ToCharArray();
        var accepted = 0;
        for (var i = 0; i < FuzzSeed.Cases(4000); i++)
        {
            var rng = FuzzSeed.For("name", i);
            var length = rng.Next(0, 40);
            var chars = new char[length];
            for (var c = 0; c < length; c++) chars[c] = alphabet[rng.Next(alphabet.Length)];
            var name = new string(chars);

            var ok = NerdbankMux.TryParseName(name, out var host, out var port);
            if (!ok)
            {
                Assert.Equal(string.Empty, host);
                continue;
            }

            accepted++;
            Assert.NotEqual(string.Empty, host);
            Assert.DoesNotContain(':', host);           // لا IPv6 حرفي: سياسة الخروج ترفضه أصلًا
            Assert.InRange(port, 1, 65535);
            Assert.StartsWith(host + ":", name, StringComparison.Ordinal); // المضيف شريحة من الاسم لا اشتقاق منه
        }
        Assert.True(accepted > 0, "the generator never produced a parsable name; the alphabet is wrong");
    }

    /// <summary>أسماء طويلة جدًا ومعرّفات غير معروفة: الحد 300 محرف في المحلل يمنع تخزين أسماء بلا سقف.</summary>
    [Fact]
    public void TryParseName_RejectsUnboundedNames()
    {
        Assert.False(NerdbankMux.TryParseName(new string('a', 4096) + ":443", out _, out _));
        Assert.False(NerdbankMux.TryParseName(new string('a', 301) + ":443", out _, out _));
        Assert.True(NerdbankMux.TryParseName(new string('a', 290) + ":443", out _, out _));
    }
}
