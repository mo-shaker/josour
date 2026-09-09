using System.Diagnostics;
using System.Threading.Channels;

namespace Josour.Tunnel.Tests.Perf;

/// <summary>
/// خصائص وصلة شبكة واحدة باتجاه واحد. الوصلة كاملة الازدواج: <see cref="LinkMedium"/> واحد لكل اتجاه.
/// </summary>
/// <remarks>
/// <para><b>ما يُنمذَج ولماذا:</b> ما تحته وما فوقه تيار بايتات TCP موثوق ومرتّب، فالنمذجة تقع على ما يراه
/// المستقبل <i>بعد</i> إعادة تجميع TCP:</para>
/// <list type="bullet">
///   <item><b>التأخير</b> (<see cref="OneWayLatency"/>): نصف الـ RTT في كل اتجاه. هذا هو المتغير الحاكم لبروتوكول
///     ذي نافذة: السقف لكل stream = النافذة ÷ RTT.</item>
///   <item><b>سقف النطاق</b> (<see cref="BitsPerSecond"/>): طابور تسلسل (serialization) مشترك؛ الكتابة الثانية
///     لا تبدأ قبل أن تفرغ الأولى من السلك. مشاركة نفس <see cref="LinkMedium"/> بين عدة streams تعطي
///     عنق زجاجة واحدًا مشتركًا كما في وصلة حقيقية.</item>
///   <item><b>الارتجاف</b> (<see cref="Jitter"/>): تباين في زمن الوصول، <b>بلا</b> إعادة ترتيب — المستقبل خلف TCP
///     لا يرى إعادة ترتيب أبدًا، بل تباينًا في وقت تسليم البايتات. الوصول مقيَّد بالرتابة لهذا السبب.</item>
///   <item><b>الفقد</b> (<see cref="LossRate"/>): لا يمكن «إسقاط» بايتات من تيار موثوق دون إفساده. الفقد يظهر
///     للطبقة الأعلى كـ <b>حجب رأس الطابور</b>: الحزمة المفقودة تؤخر كل ما بعدها حتى تُعاد. لذلك يُنمذَج
///     كتأخير تراكمي (<see cref="LossPenalty"/> لكل حزمة مفقودة) يصيب البايتات التالية كلها.</item>
/// </list>
/// <para><b>ما لا يُنمذَج:</b> نافذة ازدحام TCP وبدؤها البطيء (slow start). أي أن الأرقام <i>متفائلة</i> بمقدار
/// أول بضع RTT من كل اتصال TCP؛ ذلك يصب في مصلحة القياس المحافظ لأن النفق اتصال واحد طويل العمر
/// (يدفع بدء بطيء واحدًا) بينما المسار المباشر يفتح اتصالًا لكل مورد (يدفعه لكل مورد).</para>
/// </remarks>
internal sealed record LinkProfile
{
    /// <summary>تأخير الاتجاه الواحد = RTT ÷ 2.</summary>
    public TimeSpan OneWayLatency { get; init; } = TimeSpan.Zero;

    /// <summary>تباين موحّد ± حول التأخير. لا يعيد الترتيب (انظر ملاحظات النوع).</summary>
    public TimeSpan Jitter { get; init; } = TimeSpan.Zero;

    /// <summary>سقف النطاق بالبت/الثانية؛ 0 = بلا سقف.</summary>
    public long BitsPerSecond { get; init; }

    /// <summary>احتمال فقد الحزمة (لكل <see cref="PacketBytes"/> بايت)؛ 0 = بلا فقد.</summary>
    public double LossRate { get; init; }

    /// <summary>عقوبة إعادة الإرسال لكل حزمة مفقودة (RTO). الافتراضي 200 ms كأدنى RTO في TCP.</summary>
    public TimeSpan LossPenalty { get; init; } = TimeSpan.FromMilliseconds(200);

    /// <summary>حجم «الحزمة» لحساب الفقد (MSS نموذجي).</summary>
    public int PacketBytes { get; init; } = 1460;

    /// <summary>أقصى امتلاء لطابور السلك قبل أن يُبطَّأ الكاتب (نمذجة مخزن محدود بدل نمو بلا حد).</summary>
    public TimeSpan MaxQueue { get; init; } = TimeSpan.FromSeconds(1);

    public TimeSpan Rtt => OneWayLatency * 2;

    /// <summary>ملف بـ RTT بالمللي ثانية (يُقسَم على الاتجاهين) وسقف نطاق اختياري بالميغابت/الثانية.</summary>
    public static LinkProfile FromRtt(double rttMs, double megabitsPerSecond = 0) => new()
    {
        OneWayLatency = TimeSpan.FromMilliseconds(rttMs / 2.0),
        BitsPerSecond = (long)(megabitsPerSecond * 1_000_000),
        LossPenalty = TimeSpan.FromMilliseconds(Math.Max(200, rttMs)),
    };

    public override string ToString()
    {
        var bw = BitsPerSecond > 0 ? $", {BitsPerSecond / 1_000_000.0:0.##} Mbit/s" : ", unmetered";
        var jitter = Jitter > TimeSpan.Zero ? $", ±{Jitter.TotalMilliseconds:0.#} ms jitter" : string.Empty;
        var loss = LossRate > 0 ? $", {LossRate:P2} loss" : string.Empty;
        return $"RTT {Rtt.TotalMilliseconds:0.#} ms{bw}{jitter}{loss}";
    }
}

/// <summary>ما يُحسب لكتابة واحدة: متى تصل، وكم يبلغ امتلاء طابور السلك وقتها.</summary>
internal readonly record struct LinkSchedule(double ArrivalMs, double QueueMs);

/// <summary>
/// السلك نفسه باتجاه واحد: ساعة مشتركة، طابور تسلسل مشترك، وتأخير تراكمي من الفقد.
/// عدة <see cref="LatencyStream"/> تتشارك نسخة واحدة ⇒ تتشارك عنق الزجاجة نفسه.
/// </summary>
internal sealed class LinkMedium
{
    private readonly object _gate = new();
    private readonly Random _random;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private double _freeAtMs;
    private double _lastArrivalMs;
    private double _lossDelayMs;
    private long _bytes;
    private long _lossEvents;

    public LinkMedium(LinkProfile profile, int seed = 20260905)
    {
        Profile = profile;
        _random = new Random(seed);
    }

    public LinkProfile Profile { get; }
    public double NowMs => _clock.Elapsed.TotalMilliseconds;
    public long BytesScheduled { get { lock (_gate) return _bytes; } }
    public long LossEvents { get { lock (_gate) return _lossEvents; } }

    /// <summary>يحجز السلك لهذه الكتابة ويعيد وقت وصولها المطلق على ساعة السلك.</summary>
    public LinkSchedule Schedule(int bytes, double nowMs)
    {
        lock (_gate)
        {
            var start = Math.Max(nowMs, _freeAtMs);
            var serializationMs = Profile.BitsPerSecond > 0 ? bytes * 8_000.0 / Profile.BitsPerSecond : 0;
            _freeAtMs = start + serializationMs;

            if (Profile.LossRate > 0)
            {
                var packets = Math.Max(1, (bytes + Profile.PacketBytes - 1) / Profile.PacketBytes);
                for (var i = 0; i < packets; i++)
                {
                    if (_random.NextDouble() >= Profile.LossRate) continue;
                    _lossEvents++;
                    _lossDelayMs += Profile.LossPenalty.TotalMilliseconds;
                }
            }

            var jitterMs = Profile.Jitter > TimeSpan.Zero
                ? (_random.NextDouble() * 2 - 1) * Profile.Jitter.TotalMilliseconds
                : 0;
            var arrival = _freeAtMs + Profile.OneWayLatency.TotalMilliseconds + _lossDelayMs + jitterMs;
            // تيار بايتات لا يعيد الترتيب: الوصول رتيب مهما فعل الارتجاف.
            if (arrival < _lastArrivalMs) arrival = _lastArrivalMs;
            _lastArrivalMs = arrival;
            _bytes += bytes;
            return new LinkSchedule(arrival, _freeAtMs - nowMs);
        }
    }
}

/// <summary>
/// غلاف <see cref="Stream"/> يحوّل وصلة loopback إلى وصلة واسعة النطاق: الكتابة تُجدوَل على
/// <see cref="LinkMedium"/> وتُسلَّم إلى الـ stream الداخلي في وقتها؛ القراءة تمرّ كما هي (التأخير مطبَّق
/// بالفعل على الطرف الكاتب). التركيب: مقبس خام ← LatencyStream ← TLS ← Mux.
/// </summary>
internal sealed class LatencyStream : Stream
{
    private readonly Stream _inner;
    private readonly LinkMedium _medium;
    private readonly Channel<Segment> _queue = Channel.CreateUnbounded<Segment>(new UnboundedChannelOptions { SingleReader = true });
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _pump;
    private readonly bool _ownsInner;
    private int _disposed;

    public LatencyStream(Stream inner, LinkMedium medium, bool ownsInner = true)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _medium = medium ?? throw new ArgumentNullException(nameof(medium));
        _ownsInner = ownsInner;
        _pump = Task.Run(PumpAsync);
    }

    private readonly record struct Segment(byte[] Data, double ArrivalMs);

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        => _inner.ReadAsync(buffer, cancellationToken);

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => _inner.ReadAsync(buffer, offset, count, cancellationToken);

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (buffer.Length == 0) return;
        var schedule = _medium.Schedule(buffer.Length, _medium.NowMs);
        var overflowMs = schedule.QueueMs - _medium.Profile.MaxQueue.TotalMilliseconds;
        // مخزن محدود: الكاتب يُبطَّأ بدل أن ينمو الطابور بلا حد (كما يفعل ضغط TCP العكسي).
        if (overflowMs > 0) await Task.Delay(TimeSpan.FromMilliseconds(overflowMs), cancellationToken).ConfigureAwait(false);
        if (!_queue.Writer.TryWrite(new Segment(buffer.ToArray(), schedule.ArrivalMs)))
            throw new IOException("link is closed");
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    private async Task PumpAsync()
    {
        var ct = _cts.Token;
        try
        {
            while (await _queue.Reader.WaitToReadAsync(ct).ConfigureAwait(false))
            {
                while (_queue.Reader.TryRead(out var segment))
                {
                    // أوقات الوصول مطلقة، فخطأ مؤقّت واحد لا يتراكم على ما بعده.
                    var waitMs = segment.ArrivalMs - _medium.NowMs;
                    if (waitMs > 0.5) await Task.Delay(TimeSpan.FromMilliseconds(waitMs), ct).ConfigureAwait(false);
                    await _inner.WriteAsync(segment.Data, ct).ConfigureAwait(false);
                    await _inner.FlushAsync(ct).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception) { /* الطرف الآخر أغلق المقبس */ }
    }

    public override void Flush() { }
    public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
    public override void Write(byte[] buffer, int offset, int count) => WriteAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    /// <summary>يُفرِّغ ما تبقى في السلك (بحد أقصى) ثم يغلق.</summary>
    public async Task DrainAsync(TimeSpan timeout)
    {
        _queue.Writer.TryComplete();
        try { await _pump.WaitAsync(timeout).ConfigureAwait(false); } catch (TimeoutException) { }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _queue.Writer.TryComplete();
            // مهلة قصيرة لتسليم ما في السلك، ثم إسقاط النقل.
            try { _pump.Wait(TimeSpan.FromMilliseconds(250)); } catch { /* تجاهل */ }
            _cts.Cancel();
            if (_ownsInner) { try { _inner.Dispose(); } catch { /* تجاهل */ } }
            _cts.Dispose();
        }
        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _queue.Writer.TryComplete();
            try { await _pump.WaitAsync(TimeSpan.FromMilliseconds(250)).ConfigureAwait(false); } catch { /* تجاهل */ }
            _cts.Cancel();
            if (_ownsInner) { try { await _inner.DisposeAsync().ConfigureAwait(false); } catch { /* تجاهل */ } }
            _cts.Dispose();
        }
        GC.SuppressFinalize(this);
    }
}

/// <summary>وصلة كاملة الازدواج: وسطان (اتجاه لكل طرف) يُغلِّفان طرفَي زوج مقابس.</summary>
internal sealed class SimulatedLink
{
    public SimulatedLink(LinkProfile profile, int seed = 20260905)
    {
        Profile = profile;
        AtoB = new LinkMedium(profile, seed);
        BtoA = new LinkMedium(profile, seed + 1);
    }

    public LinkProfile Profile { get; }
    /// <summary>السلك من الطرف A إلى B (يُطبَّق على كتابات A).</summary>
    public LinkMedium AtoB { get; }
    public LinkMedium BtoA { get; }

    public Stream WrapA(Stream raw) => new LatencyStream(raw, AtoB);
    public Stream WrapB(Stream raw) => new LatencyStream(raw, BtoA);
}
