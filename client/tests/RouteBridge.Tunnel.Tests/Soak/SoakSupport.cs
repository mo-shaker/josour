using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace RouteBridge.Tunnel.Tests.Soak;

/// <summary>
/// إعداد تشغيل التحمّل، كله من متغيرات البيئة حتى يعمل بلا مراقبة ولا إعادة ترجمة:
/// <code>
///   ROUTEBRIDGE_SOAK_MINUTES   المدة بالدقائق (الافتراضي 30؛ 240 وأكثر مدعومة)
///   ROUTEBRIDGE_SOAK_SAMPLE_S  الفاصل بين العيّنات بالثواني (الافتراضي 15)
///   ROUTEBRIDGE_SOAK_RTT_MS    زمن الذهاب والإياب على الوصلة المُحاكاة (الافتراضي 150)
///   ROUTEBRIDGE_SOAK_OUT       مجلد يُكتب فيه &lt;اسم التشغيل&gt;.jsonl و&lt;اسم التشغيل&gt;.summary.json
/// </code>
/// </summary>
internal sealed record SoakOptions(TimeSpan Duration, TimeSpan SampleInterval, double RttMs, string? OutputDirectory)
{
    public static SoakOptions FromEnvironment(double defaultMinutes = 30)
    {
        var minutes = Number("ROUTEBRIDGE_SOAK_MINUTES", defaultMinutes);
        var sample = Number("ROUTEBRIDGE_SOAK_SAMPLE_S", 15);
        var rtt = Number("ROUTEBRIDGE_SOAK_RTT_MS", 150);
        var directory = Environment.GetEnvironmentVariable("ROUTEBRIDGE_SOAK_OUT");
        return new SoakOptions(
            TimeSpan.FromMinutes(Math.Max(0.05, minutes)),
            TimeSpan.FromSeconds(Math.Max(1, sample)),
            Math.Max(0, rtt),
            string.IsNullOrWhiteSpace(directory) ? null : directory);
    }

    private static double Number(string name, double fallback)
        => double.TryParse(Environment.GetEnvironmentVariable(name), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : fallback;
}

/// <summary>عيّنة واحدة. كل حقل رقم واحد حتى يصير السطر JSON قابلًا للرسم مباشرة.</summary>
internal sealed record SoakSample(
    double ElapsedSeconds,
    long ManagedHeapBytes,
    long WorkingSetBytes,
    int Gen0,
    int Gen1,
    int Gen2,
    int ThreadCount,
    int OpenHandles,
    int GuestOpenStreams,
    int HostOpenStreams,
    int GuestPendingPings,
    int HostPendingPings,
    long StreamsOpened,
    long StreamsReset,
    long BytesMoved,
    double LatencyP50Ms,
    double LatencyP95Ms,
    double LatencyMaxMs)
{
    public string ToJsonLine() => JsonSerializer.Serialize(this);
}

/// <summary>
/// يقرأ حالة العملية. <see cref="OpenHandles"/> ليس رقمًا واحدًا في كل نظام:
/// <c>Process.HandleCount</c> غير مدعوم على macOS، فالبديل عدّ واصفات الملفات المفتوحة من نظام الملفات.
/// المهم في التحمّل هو <b>الاتجاه</b> لا القيمة المطلقة، وكلا المصدرين يعطي الاتجاه نفسه.
/// </summary>
internal static class ProcessProbe
{
    private static int _forcedGen0;
    private static int _forcedGen1;
    private static int _forcedGen2;

    /// <summary>
    /// عمليات الجمع التي أجراها <see cref="Settled()"/> نفسه. تُطرح من عدّادات الجمع في العيّنة، وإلا كان العمود
    /// قياسًا للمقياس لا لضغط التخصيص: قياس الذاكرة المستقرة يفرض ثلاث عمليات جمع كاملة في كل عيّنة، وعلى نفق
    /// خامل تكون هي <b>كل</b> ما يظهر في العمود.
    /// </summary>
    public static (int Gen0, int Gen1, int Gen2) ForcedCollections
        => (Volatile.Read(ref _forcedGen0), Volatile.Read(ref _forcedGen1), Volatile.Read(ref _forcedGen2));

    public static long ManagedHeap(bool settle) => settle ? Settled() : GC.GetTotalMemory(forceFullCollection: false);

    /// <summary>ذاكرة مدارة بعد جمع كامل: الرقم الوحيد الذي يفرّق بين قمامة لم تُجمع بعد وبين تسريب.</summary>
    public static long Settled()
    {
        var g0 = GC.CollectionCount(0);
        var g1 = GC.CollectionCount(1);
        var g2 = GC.CollectionCount(2);
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        var bytes = GC.GetTotalMemory(forceFullCollection: true);
        Interlocked.Add(ref _forcedGen0, GC.CollectionCount(0) - g0);
        Interlocked.Add(ref _forcedGen1, GC.CollectionCount(1) - g1);
        Interlocked.Add(ref _forcedGen2, GC.CollectionCount(2) - g2);
        return bytes;
    }

    public static long WorkingSet()
    {
        try { using var process = Process.GetCurrentProcess(); return process.WorkingSet64; }
        catch (Exception) { return 0; }
    }

    public static int ThreadCount()
    {
        try { using var process = Process.GetCurrentProcess(); return process.Threads.Count; }
        catch (Exception) { return 0; }
    }

    /// <summary>عدد المقابس والملفات المفتوحة. صفر = لا مصدر متاح على هذا النظام (لا «لا تسريب»).</summary>
    public static int OpenHandles()
    {
        foreach (var directory in new[] { "/proc/self/fd", "/dev/fd" })
        {
            try
            {
                if (Directory.Exists(directory)) return Directory.GetFileSystemEntries(directory).Length;
            }
            catch (Exception) { /* جرّب التالي */ }
        }
        try { using var process = Process.GetCurrentProcess(); return process.HandleCount; }
        catch (Exception) { return 0; }
    }
}

/// <summary>
/// خط اتجاه على سلسلة عيّنات: الميل بالانحدار الخطي، ومقارنة النصف الأول بالنصف الثاني. المعيار المعلن في
/// مهمة الأسبوع 6: <b>«كل ما ينمو رتيبًا تسريب»</b>، فالحكم على النمو عبر المدة لا على قمة لحظية.
/// </summary>
internal readonly record struct Trend(double SlopePerHour, double FirstHalfMedian, double SecondHalfMedian, double Min, double Max, int Count)
{
    public double Growth => SecondHalfMedian - FirstHalfMedian;

    public static Trend Of(IReadOnlyList<SoakSample> samples, Func<SoakSample, double> select)
    {
        if (samples.Count == 0) return new Trend(0, 0, 0, 0, 0, 0);
        var values = samples.Select(select).ToArray();
        var times = samples.Select(s => s.ElapsedSeconds).ToArray();

        var meanT = times.Average();
        var meanV = values.Average();
        double covariance = 0;
        double variance = 0;
        for (var i = 0; i < values.Length; i++)
        {
            var dt = times[i] - meanT;
            covariance += dt * (values[i] - meanV);
            variance += dt * dt;
        }
        var slopePerSecond = variance > 0 ? covariance / variance : 0;

        var half = values.Length / 2;
        var first = half > 0 ? Median(values[..half]) : Median(values);
        var second = half > 0 ? Median(values[half..]) : Median(values);
        return new Trend(slopePerSecond * 3600, first, second, values.Min(), values.Max(), values.Length);
    }

    private static double Median(IEnumerable<double> values)
    {
        var sorted = values.OrderBy(v => v).ToArray();
        return sorted.Length == 0 ? 0 : sorted[sorted.Length / 2];
    }

    public string Describe(string unit, double scale = 1)
        => string.Create(CultureInfo.InvariantCulture,
            $"{FirstHalfMedian / scale:F2} → {SecondHalfMedian / scale:F2} {unit} (min {Min / scale:F2}, max {Max / scale:F2}, slope {SlopePerHour / scale:F2} {unit}/h)");
}

/// <summary>يجمع العيّنات، يكتبها JSONL أثناء التشغيل (فلا يضيع شيء إن قُطع)، ويبني جدول الاتجاهات في النهاية.</summary>
internal sealed class SoakRecorder : IDisposable
{
    private readonly List<SoakSample> _samples = new();
    private readonly StreamWriter? _writer;

    public SoakRecorder(string runName, SoakOptions options)
    {
        RunName = runName;
        Options = options;
        if (options.OutputDirectory is null) return;
        Directory.CreateDirectory(options.OutputDirectory);
        JsonlPath = Path.Combine(options.OutputDirectory, runName + ".jsonl");
        SummaryPath = Path.Combine(options.OutputDirectory, runName + ".summary.json");
        // بلا BOM: السطر الأول يجب أن يكون JSON صالحًا لأي قارئ، لا JSON مسبوقًا بثلاثة بايتات.
        _writer = new StreamWriter(JsonlPath, append: false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)) { AutoFlush = true };
    }

    public string RunName { get; }
    public SoakOptions Options { get; }
    public string? JsonlPath { get; }
    public string? SummaryPath { get; }
    public IReadOnlyList<SoakSample> Samples => _samples;

    public void Add(SoakSample sample)
    {
        _samples.Add(sample);
        _writer?.WriteLine(sample.ToJsonLine());
    }

    /// <summary>الاتجاهات التي تُقرأ بالعين في مخرجات الاختبار، وتُنسخ إلى <c>docs/soak-and-fuzz-week6.md</c>.</summary>
    public IReadOnlyList<(string Metric, Trend Trend, string Text)> Trends()
    {
        const double MiB = 1024 * 1024;
        return new (string, Trend, string)[]
        {
            Row("managed heap", s => s.ManagedHeapBytes, "MiB", MiB),
            Row("working set", s => s.WorkingSetBytes, "MiB", MiB),
            Row("threads", s => s.ThreadCount, "threads"),
            Row("open handles", s => s.OpenHandles, "handles"),
            Row("guest open streams", s => s.GuestOpenStreams, "streams"),
            Row("host open streams", s => s.HostOpenStreams, "streams"),
            Row("pending pings (guest)", s => s.GuestPendingPings, "pings"),
            Row("pending pings (host)", s => s.HostPendingPings, "pings"),
            Row("gen0 collections", s => s.Gen0, "collections"),
            Row("gen1 collections", s => s.Gen1, "collections"),
            Row("gen2 collections", s => s.Gen2, "collections"),
            Row("stream latency p50", s => s.LatencyP50Ms, "ms"),
            Row("stream latency p95", s => s.LatencyP95Ms, "ms"),
        };
    }

    private (string, Trend, string) Row(string metric, Func<SoakSample, double> select, string unit, double scale = 1)
    {
        var trend = Trend.Of(_samples, select);
        return (metric, trend, trend.Describe(unit, scale));
    }

    /// <summary>ملخص آلي بجانب الـ JSONL: كل ما تحتاجه لوحة أو سكربت بلا إعادة حساب.</summary>
    public void WriteSummary(IReadOnlyDictionary<string, object?> extra)
    {
        if (SummaryPath is null) return;
        var payload = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["run"] = RunName,
            ["duration_minutes"] = Options.Duration.TotalMinutes,
            ["sample_interval_seconds"] = Options.SampleInterval.TotalSeconds,
            ["rtt_ms"] = Options.RttMs,
            ["samples"] = _samples.Count,
            ["trends"] = Trends().ToDictionary(
                t => t.Metric,
                t => (object?)new Dictionary<string, object?>
                {
                    ["first_half_median"] = t.Trend.FirstHalfMedian,
                    ["second_half_median"] = t.Trend.SecondHalfMedian,
                    ["growth"] = t.Trend.Growth,
                    ["slope_per_hour"] = t.Trend.SlopePerHour,
                    ["min"] = t.Trend.Min,
                    ["max"] = t.Trend.Max,
                },
                StringComparer.Ordinal),
        };
        foreach (var (key, value) in extra) payload[key] = value;
        File.WriteAllText(SummaryPath, JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
    }

    public void Dispose() => _writer?.Dispose();
}

/// <summary>زمن الفتح إلى أول بايت لكل stream خلال نافذة عيّنة واحدة. يُفرَّغ بعد كل عيّنة.</summary>
internal sealed class LatencyWindow
{
    private readonly List<double> _values = new();
    private readonly object _gate = new();

    public void Add(double milliseconds)
    {
        lock (_gate) _values.Add(milliseconds);
    }

    public (double P50, double P95, double Max) DrainPercentiles()
    {
        double[] values;
        lock (_gate)
        {
            values = _values.ToArray();
            _values.Clear();
        }
        if (values.Length == 0) return (0, 0, 0);
        Array.Sort(values);
        return (At(values, 0.50), At(values, 0.95), values[^1]);
    }

    private static double At(double[] sorted, double p)
        => sorted[Math.Clamp((int)Math.Round((sorted.Length - 1) * p, MidpointRounding.AwayFromZero), 0, sorted.Length - 1)];
}
