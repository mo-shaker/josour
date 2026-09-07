using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using Nerdbank.Streams;
using Josour.Core.Tunnel;
using Josour.Tunnel.Mux;
using Josour.Tunnel.Tests.Mux;

namespace Josour.Tunnel.Tests.Fuzz;

/// <summary>
/// كل حالة fuzz مبذورة (seeded) فتُعاد بحرفيتها: الفشل يُعاد إنتاجه بتمرير البذرة نفسها. لا عشوائية غير مبذورة
/// في أي مكان من هذا المجلد.
/// </summary>
internal static class FuzzSeed
{
    /// <summary>البذرة الجذر. تغييرها يغيّر كل الحالات، فلا تُغيَّر إلا عمدًا.</summary>
    public const int Root = 20260906;

    public static Random For(string scope, int iteration)
        => new(HashCode.Combine(Root, StringComparer.Ordinal.GetHashCode(scope), iteration));

    /// <summary>عدد الحالات: <paramref name="quick"/> في المجموعة الافتراضية، ويرفعه متغير البيئة للتشغيل الطويل.</summary>
    public static int Cases(int quick)
    {
        var text = Environment.GetEnvironmentVariable("ROUTEBRIDGE_FUZZ_CASES");
        return int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value) && value > 0 ? value : quick;
    }
}

/// <summary>تحويلات على مخزن بايتات مسجَّل من جلسة حقيقية. كلها حتمية بالنسبة إلى <see cref="Random"/> المعطى.</summary>
internal static class Mutate
{
    public static byte[] Random(Random rng, int length)
    {
        var bytes = new byte[length];
        rng.NextBytes(bytes);
        return bytes;
    }

    public static byte[] BitFlips(byte[] input, Random rng, int flips)
    {
        var copy = (byte[])input.Clone();
        if (copy.Length == 0) return copy;
        for (var i = 0; i < flips; i++)
        {
            var index = rng.Next(copy.Length);
            copy[index] ^= (byte)(1 << rng.Next(8));
        }
        return copy;
    }

    /// <summary>قطع في موضع عشوائي: إطار نصفي على السلك.</summary>
    public static byte[] Truncate(byte[] input, Random rng)
        => input.Length == 0 ? input : input[..rng.Next(input.Length)];

    /// <summary>حذف شريحة من الوسط: أطوال تعلن أكثر مما وصل.</summary>
    public static byte[] Delete(byte[] input, Random rng)
    {
        if (input.Length < 4) return input;
        var start = rng.Next(input.Length - 1);
        var length = rng.Next(1, Math.Min(64, input.Length - start));
        var copy = new byte[input.Length - length];
        input.AsSpan(0, start).CopyTo(copy);
        input.AsSpan(start + length).CopyTo(copy.AsSpan(start));
        return copy;
    }

    /// <summary>تكرار شريحة: إطار مُعاد أو نصف إطار مُقحَم.</summary>
    public static byte[] Duplicate(byte[] input, Random rng)
    {
        if (input.Length < 4) return input;
        var start = rng.Next(input.Length - 1);
        var length = rng.Next(1, Math.Min(128, input.Length - start));
        var copy = new byte[input.Length + length];
        input.AsSpan(0, start + length).CopyTo(copy);
        input.AsSpan(start, length).CopyTo(copy.AsSpan(start + length));
        input.AsSpan(start + length).CopyTo(copy.AsSpan(start + 2 * length));
        return copy;
    }

    /// <summary>‏0xFF على شريحة: أي حقل طول داخلها يصير أكبر ما يمكن (طلب تخصيص ضخم).</summary>
    public static byte[] Saturate(byte[] input, Random rng)
    {
        var copy = (byte[])input.Clone();
        if (copy.Length == 0) return copy;
        var start = rng.Next(copy.Length);
        var length = Math.Min(rng.Next(1, 9), copy.Length - start);
        copy.AsSpan(start, length).Fill(0xFF);
        return copy;
    }

    /// <summary>إعادة ترتيب كتل: إطارات كاملة تصل بغير ترتيبها.</summary>
    public static byte[] Reorder(byte[] input, Random rng)
    {
        if (input.Length < 8) return input;
        var blocks = new List<byte[]>();
        var offset = 0;
        while (offset < input.Length)
        {
            var length = Math.Min(rng.Next(1, 97), input.Length - offset);
            blocks.Add(input[offset..(offset + length)]);
            offset += length;
        }
        for (var i = blocks.Count - 1; i > 0; i--)
        {
            var j = rng.Next(i + 1);
            (blocks[i], blocks[j]) = (blocks[j], blocks[i]);
        }
        return blocks.SelectMany(b => b).ToArray();
    }

    /// <summary>يختار تحويلًا واحدًا بحسب البذرة، ويعيد اسمه مع الناتج (الاسم يظهر في رسالة الفشل).</summary>
    public static (string Name, byte[] Bytes) Any(byte[] corpus, Random rng)
        => rng.Next(8) switch
        {
            0 => ("random", Random(rng, rng.Next(1, 4096))),
            1 => ("bitflips", BitFlips(corpus, rng, rng.Next(1, 32))),
            2 => ("truncate", Truncate(corpus, rng)),
            3 => ("delete", Delete(corpus, rng)),
            4 => ("duplicate", Duplicate(corpus, rng)),
            5 => ("saturate", Saturate(corpus, rng)),
            6 => ("reorder", Reorder(corpus, rng)),
            _ => ("truncated-bitflips", BitFlips(Truncate(corpus, rng), rng, rng.Next(1, 16))),
        };
}

/// <summary>غلاف يمرر كل شيء ويحتفظ بنسخة من كل ما كُتب: يبني مخزن الـ fuzz من حركة مرور حقيقية لا من التخمين.</summary>
internal sealed class RecordingStream : Stream
{
    private readonly Stream _inner;
    private readonly MemoryStream _written = new();
    private readonly object _gate = new();

    public RecordingStream(Stream inner) => _inner = inner;

    public byte[] Recorded { get { lock (_gate) return _written.ToArray(); } }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        => await _inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        lock (_gate) _written.Write(buffer.Span);
        await _inner.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override void Flush() => _inner.Flush();
    public override Task FlushAsync(CancellationToken cancellationToken) => _inner.FlushAsync(cancellationToken);
    public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
    public override void Write(byte[] buffer, int offset, int count) => WriteAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing) _inner.Dispose();
        base.Dispose(disposing);
    }
}

/// <summary>
/// الحالة النهائية المقبولة لأي <see cref="NerdbankMux"/> بعد إدخال معادٍ: إما اكتمال نظيف (GOAWAY متبادل)،
/// وإما عطل بـ <see cref="MuxClosedException"/> وحده. أي نوع استثناء آخر، أو عدم الاكتمال، عيب.
/// </summary>
internal readonly record struct MuxVerdict(bool Completed, bool Faulted, Exception? Error)
{
    public string Describe() => !Completed ? "did not complete" : Faulted ? $"faulted with {Error?.GetType().Name}: {Error?.Message}" : "completed cleanly";
}

/// <summary>
/// ضحية fuzz: <see cref="NerdbankMux"/> فوق طرف من زوج مقابس، والطرف الآخر بيد الاختبار يكتب فيه ما يشاء.
/// مضخة تفريغ دائمة على جانب المهاجم حتى لا تتوقف كتابات الضحية على مخزن ممتلئ فيبدو التعليق تعليقًا وهو ليس منه.
/// </summary>
internal sealed class MuxVictim : IAsyncDisposable
{
    private static readonly TimeSpan Settle = TimeSpan.FromSeconds(20);

    private readonly Stream _attacker;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _drain;
    private long _drained;

    private MuxVictim(NerdbankMux mux, Stream attacker)
    {
        Mux = mux;
        _attacker = attacker;
        _drain = Task.Run(DrainAsync);
    }

    public NerdbankMux Mux { get; }
    public long Drained => Volatile.Read(ref _drained);

    public static async Task<MuxVictim> CreateAsync(TunnelRole role = TunnelRole.Host, MuxOptions? options = null)
    {
        var (victimSide, attackerSide) = await Loopback.CreatePairAsync();
        NerdbankMux mux;
        try
        {
            mux = NerdbankMux.Create(victimSide, role, options ?? new MuxOptions { EnableLiveness = false });
        }
        catch
        {
            await victimSide.DisposeAsync();
            await attackerSide.DisposeAsync();
            throw;
        }
        return new MuxVictim(mux, attackerSide);
    }

    /// <summary>يكتب الحمولة دفعة واحدة.</summary>
    public Task FeedAsync(byte[] payload) => _attacker.WriteAsync(payload, _cts.Token).AsTask();

    /// <summary>
    /// يكتب الحمولة على قطع صغيرة (1..<paramref name="maxChunk"/> بايت) مع دفع بعد كل قطعة: هذا هو الشكل الذي
    /// يجبر أي محلل على التعامل مع إطار مقسوم على عدة قراءات.
    /// </summary>
    public async Task FeedFragmentedAsync(byte[] payload, Random rng, int maxChunk = 3)
    {
        var offset = 0;
        while (offset < payload.Length)
        {
            var length = Math.Min(rng.Next(1, maxChunk + 1), payload.Length - offset);
            await _attacker.WriteAsync(payload.AsMemory(offset, length), _cts.Token).ConfigureAwait(false);
            await _attacker.FlushAsync(_cts.Token).ConfigureAwait(false);
            offset += length;
        }
    }

    /// <summary>يقطع السلك (EOF عند الضحية) ثم ينتظر استقرار <see cref="NerdbankMux.Completion"/>.</summary>
    public async Task<MuxVerdict> CloseAndSettleAsync(TimeSpan? within = null)
    {
        try { await _attacker.FlushAsync(CancellationToken.None).ConfigureAwait(false); } catch { /* أُغلق */ }
        try { _attacker.Dispose(); } catch { /* أُغلق */ }
        return await SettleAsync(within).ConfigureAwait(false);
    }

    /// <summary>ينتظر اكتمال <see cref="NerdbankMux.Completion"/> بلا قطع السلك.</summary>
    public async Task<MuxVerdict> SettleAsync(TimeSpan? within = null)
    {
        try
        {
            await Mux.Completion.WaitAsync(within ?? Settle).ConfigureAwait(false);
            return new MuxVerdict(true, false, null);
        }
        catch (TimeoutException)
        {
            return new MuxVerdict(false, false, null);
        }
        catch (Exception e)
        {
            return new MuxVerdict(true, true, e);
        }
    }

    private async Task DrainAsync()
    {
        var buffer = new byte[64 * 1024];
        try
        {
            while (true)
            {
                var n = await _attacker.ReadAsync(buffer, _cts.Token).ConfigureAwait(false);
                if (n == 0) return;
                Interlocked.Add(ref _drained, n);
            }
        }
        catch (Exception) { /* أُغلق السلك */ }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        try { await Mux.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(15)).ConfigureAwait(false); } catch { /* تجاهل */ }
        try { _attacker.Dispose(); } catch { /* تجاهل */ }
        try { await _drain.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); } catch { /* تجاهل */ }
        _cts.Dispose();
    }
}

/// <summary>
/// طرف معادٍ يتكلم بروتوكول Nerdbank 3 الصحيح لكنه يكذب فوقه: يكتب على قناة التحكم المزروعة ما يشاء، ويعرض
/// قنوات بأسماء لا يقبلها العقد، ويرد ببايتات حالة غير موجودة. هذا هو حد المسؤولية في ADR-0006: الإطارات
/// تملكها المكتبة، وما داخلها (PING/PONG/GOAWAY وبايت الحالة واسم <c>host:port</c>) نملكه نحن.
/// </summary>
internal sealed class RawMuxPeer : IAsyncDisposable
{
    private readonly MultiplexingStream _mx;
    private readonly Stream _transport;

    private readonly CancellationTokenSource _cts = new();
    private readonly Task _controlDrain;
    private int _pongs;
    private int _pings;
    private int _goAways;
    private int _unknown;

    private RawMuxPeer(MultiplexingStream mx, MultiplexingStream.Channel control, Stream transport, NerdbankMux victim)
    {
        _mx = mx;
        Control = control;
        _transport = transport;
        Victim = victim;
        _controlDrain = Task.Run(DrainControlAsync);
    }

    /// <summary>الضحية: <see cref="NerdbankMux"/> حقيقي بكل حلقاته.</summary>
    public NerdbankMux Victim { get; }

    /// <summary>القناة المزروعة (المعرّف 0) كما يراها المعادي: مكان إطارات PING/PONG/GOAWAY.</summary>
    public MultiplexingStream.Channel Control { get; }

    public MultiplexingStream Mx => _mx;

    /// <summary>ما وصل من الضحية على قناة التحكم، مصنَّفًا بنوع الإطار في عقدنا.</summary>
    public int PongsReceived => Volatile.Read(ref _pongs);
    public int PingsReceived => Volatile.Read(ref _pings);
    public int GoAwaysReceived => Volatile.Read(ref _goAways);
    public int UnknownFramesReceived => Volatile.Read(ref _unknown);

    /// <summary>سبب آخر GOAWAY وصل من الضحية (بايت الحمولة الأول كما هو على السلك).</summary>
    public byte? LastGoAwayReason { get; private set; }

    /// <summary>هل يرد المعادي على PING بـ PONG؟ لازم لأي اختبار يستدعي <c>Victim.PingAsync</c>.</summary>
    public bool AnswerPings { get; set; }

    /// <summary>ينتظر وصول GOAWAY من الضحية.</summary>
    public async Task<bool> WaitForGoAwayAsync(TimeSpan within)
    {
        var deadline = DateTime.UtcNow + within;
        while (DateTime.UtcNow < deadline)
        {
            if (GoAwaysReceived > 0) return true;
            await Task.Delay(10).ConfigureAwait(false);
        }
        return GoAwaysReceived > 0;
    }

    public async Task<bool> WaitForPongsAsync(int count, TimeSpan within)
    {
        var deadline = DateTime.UtcNow + within;
        while (DateTime.UtcNow < deadline)
        {
            if (PongsReceived >= count) return true;
            await Task.Delay(5).ConfigureAwait(false);
        }
        return PongsReceived >= count;
    }

    /// <summary>
    /// يقبل كل قناة تعرضها الضحية ويكتب فيها بايت الحالة الذي يعيده <paramref name="statusFor"/>
    /// (‏<c>null</c> = لا يكتب شيئًا ويغلق فورًا). هكذا يُختبر جانب Guest من بروتوكول بايت الحالة.
    /// </summary>
    public void AcceptOffersWith(Func<string, byte?> statusFor)
    {
        _mx.ChannelOffered += (_, e) =>
        {
            if (e.IsAccepted) return;
            MultiplexingStream.Channel channel;
            try { channel = _mx.AcceptChannel(e.QualifiedId.Id); }
            catch (Exception) { return; }
            _ = Task.Run(async () =>
            {
                try
                {
                    if (statusFor(e.Name) is byte status)
                    {
                        channel.Output.GetSpan(1)[0] = status;
                        channel.Output.Advance(1);
                        await channel.Output.FlushAsync().ConfigureAwait(false);
                    }
                    channel.Output.Complete();
                    channel.Input.Complete();
                }
                catch (Exception)
                {
                    try { channel.Dispose(); } catch { /* تجاهل */ }
                }
            });
        };
    }

    /// <summary>يقرأ قناة التحكم ويصنّف كل إطار تسعة بايتات (1 PING، 2 PONG، 3 GOAWAY).</summary>
    private async Task DrainControlAsync()
    {
        var reader = Control.Input;
        var frame = new byte[9];
        try
        {
            while (true)
            {
                var result = await reader.ReadAsync(_cts.Token).ConfigureAwait(false);
                var buffer = result.Buffer;
                while (buffer.Length >= 9)
                {
                    buffer.Slice(0, 9).CopyTo(frame);
                    buffer = buffer.Slice(9);
                    switch (frame[0])
                    {
                        case 1:
                            Interlocked.Increment(ref _pings);
                            if (AnswerPings) await WriteControlFrameAsync(2, frame.AsSpan(1, 8)).ConfigureAwait(false);
                            break;
                        case 2: Interlocked.Increment(ref _pongs); break;
                        case 3: LastGoAwayReason = frame[1]; Interlocked.Increment(ref _goAways); break;
                        default: Interlocked.Increment(ref _unknown); break;
                    }
                }
                reader.AdvanceTo(buffer.Start, buffer.End);
                if (result.IsCompleted || result.IsCanceled) return;
            }
        }
        catch (Exception) { /* أُغلقت القناة */ }
    }

    public static async Task<RawMuxPeer> CreateAsync(TunnelRole victimRole = TunnelRole.Host, MuxOptions? victimOptions = null)
    {
        var (victimSide, peerSide) = await Loopback.CreatePairAsync();
        NerdbankMux victim;
        try
        {
            victim = NerdbankMux.Create(victimSide, victimRole, victimOptions ?? new MuxOptions { EnableLiveness = false });
        }
        catch
        {
            await victimSide.DisposeAsync();
            await peerSide.DisposeAsync();
            throw;
        }

        var options = new MultiplexingStream.Options
        {
            ProtocolMajorVersion = 3,
            DefaultChannelReceivingWindowSize = MuxWindow.NearWindow,
            StartSuspended = true,
            TraceSource = new TraceSource("Josour.Fuzz", SourceLevels.Off),
        };
        // نفس القناة المزروعة بنفس النافذة: الطرفان يتفقان على وجودها لا على ما يُكتب فيها.
        options.SeededChannels.Add(new MultiplexingStream.ChannelOptions { ChannelReceivingWindowSize = MuxWindow.DistantWindow });

        var mx = MultiplexingStream.Create(peerSide, options);
        mx.StartListening();
        var control = mx.AcceptChannel(0);
        return new RawMuxPeer(mx, control, peerSide, victim);
    }

    /// <summary>يكتب بايتات خامًا على قناة التحكم (إطار سليم أو نصف إطار أو هراء).</summary>
    public async Task WriteControlAsync(ReadOnlyMemory<byte> bytes)
    {
        // ‏GetSpan/Advance صراحةً: امتداد Write في Nerdbank.Streams يتنازع مع الذي في System.Buffers.
        bytes.Span.CopyTo(Control.Output.GetSpan(bytes.Length));
        Control.Output.Advance(bytes.Length);
        await Control.Output.FlushAsync().ConfigureAwait(false);
    }

    /// <summary>إطار تحكم من عقدنا: <c>u8 type | 8 بايت حمولة</c>.</summary>
    public Task WriteControlFrameAsync(byte type, ReadOnlySpan<byte> payload8)
    {
        var frame = new byte[9];
        frame[0] = type;
        payload8[..Math.Min(8, payload8.Length)].CopyTo(frame.AsSpan(1));
        return WriteControlAsync(frame);
    }

    /// <summary>يعرض قناة بالاسم المعطى ويعيد بايت الحالة الذي يرد به الضحية (‏<c>0</c> = OPEN_OK).</summary>
    public async Task<byte?> OfferAndReadStatusAsync(string name, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            var channel = await _mx.OfferChannelAsync(name, cts.Token).ConfigureAwait(false);
            try
            {
                while (true)
                {
                    var result = await channel.Input.ReadAsync(cts.Token).ConfigureAwait(false);
                    if (result.Buffer.Length >= 1)
                    {
                        var status = result.Buffer.FirstSpan[0];
                        channel.Input.AdvanceTo(result.Buffer.GetPosition(1));
                        return status;
                    }
                    if (result.IsCompleted || result.IsCanceled)
                    {
                        channel.Input.AdvanceTo(result.Buffer.End);
                        return null;
                    }
                    channel.Input.AdvanceTo(result.Buffer.Start, result.Buffer.End);
                }
            }
            finally
            {
                channel.Dispose();
            }
        }
        catch (OperationCanceledException) { return null; }
        catch (Exception) { return null; }
    }

    public async Task<MuxVerdict> SettleAsync(TimeSpan within)
    {
        try
        {
            await Victim.Completion.WaitAsync(within).ConfigureAwait(false);
            return new MuxVerdict(true, false, null);
        }
        catch (TimeoutException) { return new MuxVerdict(false, false, null); }
        catch (Exception e) { return new MuxVerdict(true, true, e); }
    }

    public async ValueTask DisposeAsync()
    {
        // ترتيب مقصود: يموت نقل المعادي أولًا. لو تخلصنا من الضحية أولًا لانتظر GoAwayDrainTimeout كاملًا في كل
        // حالة (نصف ثانية × مئات الحالات) بلا فائدة، لأن لا أحد يغلق الطرف الآخر.
        _cts.Cancel();
        try { await _mx.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false); } catch { /* تجاهل */ }
        try { await _transport.DisposeAsync().ConfigureAwait(false); } catch { /* تجاهل */ }
        try { await Victim.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(15)).ConfigureAwait(false); } catch { /* تجاهل */ }
        try { await _controlDrain.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); } catch { /* تجاهل */ }
        _cts.Dispose();
    }
}

/// <summary>مخزن الـ fuzz: بايتات جلسة نفق حقيقية كما خرجت من <see cref="NerdbankMux"/> على السلك.</summary>
internal static class MuxCorpus
{
    private static byte[]? _cached;
    private static readonly SemaphoreSlim Gate = new(1, 1);

    /// <summary>
    /// يسجّل جلسة كاملة: فتح ناجح ببيانات في الاتجاهين، فتح مرفوض بكل الأسباب، PING، إغلاق نصفي، ثم GOAWAY.
    /// الناتج يحمل إطارات العرض والقبول والمحتوى وتحديث النافذة والإغلاق، وإطارات التحكم التسعة بايتات.
    /// </summary>
    public static async Task<byte[]> GetAsync()
    {
        if (_cached is { } cached) return cached;
        await Gate.WaitAsync();
        try
        {
            if (_cached is { } inner) return inner;
            _cached = await RecordAsync();
            return _cached;
        }
        finally
        {
            Gate.Release();
        }
    }

    private static async Task<byte[]> RecordAsync()
    {
        var (guestSide, hostSide) = await Loopback.CreatePairAsync();
        var recorder = new RecordingStream(guestSide);
        var options = new MuxOptions { EnableLiveness = false, OpenTimeout = TimeSpan.FromSeconds(10) };
        var guest = NerdbankMux.Create(recorder, TunnelRole.Guest, options);
        var host = NerdbankMux.Create(hostSide, TunnelRole.Host, options);
        try
        {
            host.OpenRequested = (req, _) => Task.FromResult(
                req.Port == 443 ? MuxOpenDecision.Ok(new TestTarget(produce: 12 * 1024)) : MuxOpenDecision.Fail((OpenFailReason)req.Port));

            var open = await guest.OpenStreamAsync("example.com", 443, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(15));
            var stream = open.Stream!;
            await StreamIo.WriteAllAsync(stream, 8 * 1024);
            await ((IHalfClosable)stream).CompleteWritingAsync(CancellationToken.None);
            await StreamIo.ReadToEndAsync(stream).WaitAsync(TimeSpan.FromSeconds(15));
            await stream.DisposeAsync();

            for (var reason = 1; reason <= 7; reason++)
                await guest.OpenStreamAsync("blocked.example", reason, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(15));

            await guest.PingAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(15));
            await guest.CloseAsync(GoAwayReason.SessionEnd);
            return recorder.Recorded;
        }
        finally
        {
            await guest.DisposeAsync();
            await host.DisposeAsync();
        }
    }
}

/// <summary>
/// يراقب استثناءات المهام غير المُلاحَظة أثناء نطاق fuzz (تجميعة الاختبارات لا تتوازى، فما يُلتقط من هذا التشغيل).
///
/// <para>يُصنَّف المُلتقَط إلى ما نصنعه نحن (<see cref="IsOurs"/>: نوع في فضاء أسماء Josour) وما تصنعه
/// <c>Nerdbank.Streams</c> في مهامها الداخلية. الأول عيب يجب أن يكون صفرًا؛ الثاني ضجيج طرف ثالث لا نملك إصلاحه
/// (‏<c>Channel.AutoCloseOnPipesClosureAsync</c> يعطل بـ IOException حين يموت النقل أثناء كتابة صادرة) فيُبلَّغ عنه
/// عددًا ويُوثَّق في <c>docs/soak-and-fuzz-week6.md</c>.</para>
/// </summary>
internal sealed class UnobservedExceptionWatch : IDisposable
{
    private readonly List<Exception> _seen = new();
    private readonly object _gate = new();

    public UnobservedExceptionWatch() => TaskScheduler.UnobservedTaskException += OnUnobserved;

    /// <summary>الاستثناءات الداخلية مسطَّحة، بعد جمع كامل وتشغيل المنهيات (الحدث لا يُرفع قبلهما).</summary>
    public IReadOnlyList<Exception> Drain()
    {
        GC.Collect(2, GCCollectionMode.Forced, blocking: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Forced, blocking: true);
        lock (_gate) return _seen.SelectMany(e => e is AggregateException a ? a.Flatten().InnerExceptions.AsEnumerable() : new[] { e }).ToArray();
    }

    /// <summary>هل الاستثناء من صنعنا (نوع في Josour) لا من مهام المكتبة الداخلية؟</summary>
    public static bool IsOurs(Exception e)
        => e.GetType().Namespace?.StartsWith("Josour", StringComparison.Ordinal) == true;

    public static string Describe(IEnumerable<Exception> exceptions)
        => string.Join("\n---\n", exceptions.Select(e => e.ToString()));

    private void OnUnobserved(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        lock (_gate) _seen.Add(e.Exception!);
        e.SetObserved();
    }

    public void Dispose() => TaskScheduler.UnobservedTaskException -= OnUnobserved;
}
