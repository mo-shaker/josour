using System.Globalization;

namespace Josour.Tunnel.Mux;

/// <summary>
/// نافذة الاستقبال لكل stream والحد الأقصى للـ streams المتزامنة المرافق لها (docs/protocol.md القسم 5 بعد تعديل الأسبوع 5).
///
/// سقف الـ stream الواحد = النافذة ÷ RTT بالضبط (قياسات <c>docs/performance-week5.md</c>: كفاءة 99% إلى 102%)،
/// فالنافذة تُشتق من الـ RTT المقيس عند الاتصال (<c>connect_ms</c>) لتبقى فوق 110 Mbit/s حتى 300 ms:
///
/// <code>
/// | RTT المقيس | النافذة | الحد المتزامن | السقف الناتج            |
/// | ≤ 60 ms    | 1 MiB   | 256           | ≥ 140 Mbit/s            |
/// | ≤ 150 ms   | 2 MiB   | 128           | ≥ 110 Mbit/s عند 150 ms |
/// | &gt; 150 ms   | 4 MiB   |  64           | ≥ 110 Mbit/s عند 300 ms |
/// </code>
///
/// <b>لماذا ينزل الحد كلما كبرت النافذة:</b> النافذة رصيد لا حجز مسبق، لكن السقف النظري الأسوأ (كل الـ streams
/// متوقفة ونوافذها ممتلئة) هو الحد المتزامن × النافذة. 256 × 4 MiB = 1 GiB على جهاز قد يكون حاسوبًا شخصيًا،
/// فيُثبَّت الحاصل عند <see cref="MemoryBudget"/> = 256 MiB في كل الشرائح.
///
/// <b>لماذا لا يحتاج هذا تفاوضًا على السلك:</b> في بروتوكول Nerdbank 3 نافذة الاستقبال خاصية <i>المستقبل</i>
/// ويحملها إطار العرض/القبول لكل قناة: <c>Channel.localWindowSize</c> = ما أعلناه نحن، و<c>remoteWindowSize</c>
/// (رصيد الإرسال) = ما أعلنه الطرف الآخر. فكل طرف يحجّم نافذة استقباله وحده، واختلاف الشريحتين بين الطرفين
/// قانوني: كل اتجاه محكوم بنافذة مستقبله هو.
/// </summary>
public sealed record MuxWindow
{
    /// <summary>الشريحة الدنيا: 1 MiB (النافذة الوحيدة قبل تعديل الأسبوع 5).</summary>
    public const int NearWindow = 1024 * 1024;

    /// <summary>الشريحة الوسطى: 2 MiB. وهي أيضًا الملاذ عند <c>connect_ms</c> غير معقول.</summary>
    public const int RegionalWindow = 2 * 1024 * 1024;

    /// <summary>الشريحة العليا: 4 MiB.</summary>
    public const int DistantWindow = 4 * 1024 * 1024;

    /// <summary>ميزانية الذاكرة لكل جلسة: النافذة × الحد المتزامن (docs/protocol.md القسم 5).</summary>
    public const int MemoryBudget = 256 * 1024 * 1024;

    /// <summary>الحد الأعلى للـ streams المتزامنة مهما صغرت النافذة (القيمة الأصلية في العقد).</summary>
    public const int MaxConcurrentStreamsCap = 256;

    /// <summary>حد الـ streams عند النافذة الافتراضية؛ قيمة التوافق لمن لا يشتق شيئًا.</summary>
    public const int DefaultMaxConcurrentStreams = MaxConcurrentStreamsCap;

    /// <summary>أعلى RTT في الشريحة الدنيا.</summary>
    public static readonly TimeSpan NearRoundTrip = TimeSpan.FromMilliseconds(60);

    /// <summary>أعلى RTT في الشريحة الوسطى.</summary>
    public static readonly TimeSpan RegionalRoundTrip = TimeSpan.FromMilliseconds(150);

    /// <summary>فوق هذا لا يكون الرقم زمن ذهاب وإياب بل قياسًا فاسدًا (مهلة، ساعة قفزت، حقل مفقود).</summary>
    public static readonly TimeSpan MaxPlausibleRoundTrip = TimeSpan.FromSeconds(5);

    private MuxWindow(int receiveWindow, int maxConcurrentStreams, bool isFallback, string reason)
    {
        ReceiveWindow = receiveWindow;
        MaxConcurrentStreams = maxConcurrentStreams;
        IsFallback = isFallback;
        Reason = reason;
    }

    /// <summary>نافذة الاستقبال لكل stream بالبايت (<c>DefaultChannelReceivingWindowSize</c> في Nerdbank).</summary>
    public int ReceiveWindow { get; }

    /// <summary>أقصى عدد streams متزامنة يبقي الحاصل ضمن <see cref="MemoryBudget"/>.</summary>
    public int MaxConcurrentStreams { get; }

    /// <summary>هل جاءت من الملاذ لأن الـ RTT المعطى لم يكن رقمًا معقولًا؟</summary>
    public bool IsFallback { get; }

    /// <summary>سطر واحد يشرح الاختيار (يُسجَّل في تشخيص الجلسة؛ بلا أسرار ولا حمولات).</summary>
    public string Reason { get; }

    /// <summary>السقف النظري لـ stream واحد على هذا الـ RTT (بايت/ثانية) = النافذة ÷ RTT.</summary>
    public double CeilingBytesPerSecond(TimeSpan roundTrip)
        => roundTrip > TimeSpan.Zero ? ReceiveWindow / roundTrip.TotalSeconds : double.PositiveInfinity;

    /// <summary>ما يستهلكه أسوأ سيناريو: كل الـ streams متوقفة ونوافذها ممتلئة.</summary>
    public long WorstCaseBytes => (long)ReceiveWindow * MaxConcurrentStreams;

    /// <summary>النافذة الافتراضية بلا اشتقاق: 1 MiB و256 stream (سلوك ما قبل التعديل حرفيًا).</summary>
    public static MuxWindow Default { get; } = new(NearWindow, LimitFor(NearWindow), false, "default window (no measured round trip)");

    /// <summary>
    /// الاشتقاق الملزم في docs/protocol.md القسم 5. لا يرمي أبدًا: الرقم غير المعقول (صفر، سالب، أو أكبر من
    /// <see cref="MaxPlausibleRoundTrip"/>) يعطي الشريحة الوسطى مع <see cref="Reason"/> يشرح السبب،
    /// لأن اختيار نافذة من قياس فاسد أسوأ من اختيار الوسط.
    /// </summary>
    public static MuxWindow ForRoundTrip(TimeSpan roundTrip)
    {
        if (roundTrip <= TimeSpan.Zero || roundTrip > MaxPlausibleRoundTrip)
        {
            return new MuxWindow(
                RegionalWindow,
                LimitFor(RegionalWindow),
                true,
                $"measured round trip {Ms(roundTrip)} ms is not plausible (expected 0 < rtt <= {Ms(MaxPlausibleRoundTrip)} ms); " +
                $"falling back to the middle tier {Describe(RegionalWindow)}");
        }

        var window = roundTrip <= NearRoundTrip ? NearWindow
            : roundTrip <= RegionalRoundTrip ? RegionalWindow
            : DistantWindow;
        var bound = roundTrip <= NearRoundTrip ? $"<= {Ms(NearRoundTrip)} ms"
            : roundTrip <= RegionalRoundTrip ? $"<= {Ms(RegionalRoundTrip)} ms"
            : $"> {Ms(RegionalRoundTrip)} ms";
        return new MuxWindow(window, LimitFor(window), false, $"round trip {Ms(roundTrip)} ms {bound} => {Describe(window)}");
    }

    /// <summary>
    /// نافذة معطاة صراحةً (تجاوز <c>MuxOptions.ReceiveWindow</c> في الاختبارات وأداة Spike). الحد المتزامن يتبعها
    /// بالميزانية نفسها ما لم يُعطَ صراحةً.
    /// </summary>
    public static MuxWindow ForWindow(int receiveWindow, int? maxConcurrentStreams = null, string? reason = null)
    {
        if (receiveWindow < 1) throw new ArgumentOutOfRangeException(nameof(receiveWindow), receiveWindow, "receive window must be positive");
        if (maxConcurrentStreams is < 1) throw new ArgumentOutOfRangeException(nameof(maxConcurrentStreams), maxConcurrentStreams, "stream limit must be positive");
        var limit = maxConcurrentStreams ?? LimitFor(receiveWindow);
        return new MuxWindow(receiveWindow, limit, false, reason ?? $"explicit window {Describe(receiveWindow, limit)}");
    }

    /// <summary>يستبدل الحد المتزامن وحده (تجاوز <c>MuxOptions.MaxConcurrentStreams</c>) ويبقي سبب النافذة.</summary>
    public MuxWindow WithMaxConcurrentStreams(int maxConcurrentStreams)
    {
        if (maxConcurrentStreams < 1) throw new ArgumentOutOfRangeException(nameof(maxConcurrentStreams), maxConcurrentStreams, "stream limit must be positive");
        return maxConcurrentStreams == MaxConcurrentStreams
            ? this
            : new MuxWindow(ReceiveWindow, maxConcurrentStreams, IsFallback, $"{Reason}; stream limit overridden to {maxConcurrentStreams}");
    }

    public override string ToString() => Describe(ReceiveWindow, MaxConcurrentStreams);

    /// <summary>الحد المتزامن الذي يبقي النافذة × الحد ضمن الميزانية، بسقف 256 (قيمة العقد الأصلية).</summary>
    private static int LimitFor(int receiveWindow) => Math.Clamp(MemoryBudget / receiveWindow, 1, MaxConcurrentStreamsCap);

    private static string Describe(int receiveWindow) => Describe(receiveWindow, LimitFor(receiveWindow));

    private static string Describe(int receiveWindow, int limit)
        => string.Create(CultureInfo.InvariantCulture, $"{receiveWindow / 1024.0 / 1024.0:0.###} MiB x {limit} streams");

    private static string Ms(TimeSpan value) => value.TotalMilliseconds.ToString("0.###", CultureInfo.InvariantCulture);
}
