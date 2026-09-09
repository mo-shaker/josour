namespace Josour.Tunnel.Tests.Soak;

/// <summary>
/// وحدات أداة التحمّل نفسها. تعمل في ثوانٍ وتبقى في المجموعة الافتراضية عمدًا: أداة قياس لا يقيسها أحد تصدأ
/// بصمت، فتعطي «لا تسريب» لأنها لا ترى شيئًا. هنا يُثبت أنها ترى نموًّا حين يوجد، وتراه صفرًا حين لا يوجد.
/// </summary>
public class SoakSupportTests
{
    private static SoakSample At(double seconds, double heapBytes, int handles = 0, double latency = 0)
        => new(seconds, (long)heapBytes, 0, 0, 0, 0, 0, handles, 0, 0, 0, 0, 0, 0, 0, latency, 0, 0);

    [Fact]
    public void Trend_OnAFlatSeries_ReportsNoGrowth()
    {
        var samples = Enumerable.Range(0, 20).Select(i => At(i * 15, 10_000_000)).ToList();
        var trend = Trend.Of(samples, s => s.ManagedHeapBytes);

        Assert.Equal(0, trend.Growth);
        Assert.Equal(0, trend.SlopePerHour, 3);
        Assert.Equal(20, trend.Count);
    }

    [Fact]
    public void Trend_OnAMonotonicSeries_ReportsTheGrowthAndTheHourlySlope()
    {
        // مليون بايت في كل عيّنة، وعيّنة كل 15 ثانية ⇒ 240 مليون بايت في الساعة.
        var samples = Enumerable.Range(0, 20).Select(i => At(i * 15, 10_000_000 + i * 1_000_000d)).ToList();
        var trend = Trend.Of(samples, s => s.ManagedHeapBytes);

        Assert.True(trend.Growth > 9_000_000, $"growth was {trend.Growth}");
        Assert.InRange(trend.SlopePerHour, 239_000_000, 241_000_000);
        Assert.Equal(10_000_000, trend.Min);
        Assert.Equal(29_000_000, trend.Max);
    }

    /// <summary>
    /// تذبذب محدود (تخصيص وجمع) لا يُقرأ تسريبًا: النصفان متساويان، والميل أصغر بمرتبة على الأقل من ميل نموّ
    /// حقيقي بالسعة نفسها. المقارنة بميل مرجعي لا برقم مطلق، لأن استقراء «لكل ساعة» من نافذة دقائق يضخّم أي بقية.
    /// </summary>
    [Fact]
    public void Trend_OnAnOscillatingSeries_IsNotReadAsGrowth()
    {
        var oscillating = Enumerable.Range(0, 40).Select(i => At(i * 15, 10_000_000 + i % 2 * 4_000_000)).ToList();
        var ramp = Enumerable.Range(0, 40).Select(i => At(i * 15, 10_000_000 + i * 100_000d)).ToList();

        var wave = Trend.Of(oscillating, s => s.ManagedHeapBytes);
        var growth = Trend.Of(ramp, s => s.ManagedHeapBytes);

        Assert.Equal(wave.FirstHalfMedian, wave.SecondHalfMedian);
        Assert.True(growth.Growth > 1_500_000, $"the reference ramp only grew {growth.Growth}");
        Assert.True(Math.Abs(wave.SlopePerHour) < growth.SlopePerHour / 10,
            $"an oscillation produced a slope of {wave.SlopePerHour}/h against a real ramp's {growth.SlopePerHour}/h");
    }

    [Fact]
    public void Trend_OnAnEmptySeries_IsZero_NotAnException()
    {
        var trend = Trend.Of(Array.Empty<SoakSample>(), s => s.ManagedHeapBytes);
        Assert.Equal(0, trend.Count);
        Assert.Equal(0, trend.SlopePerHour);
    }

    [Fact]
    public void LatencyWindow_ReportsPercentiles_AndEmptiesAfterEachDrain()
    {
        var window = new LatencyWindow();
        for (var i = 1; i <= 100; i++) window.Add(i);

        var (p50, p95, max) = window.DrainPercentiles();
        Assert.Equal(51, p50);
        Assert.Equal(95, p95);
        Assert.Equal(100, max);

        Assert.Equal((0, 0, 0), window.DrainPercentiles());
    }

    [Fact]
    public void Options_ComeFromTheEnvironment_WithContractDefaults()
    {
        var defaults = SoakOptions.FromEnvironment();
        Assert.True(defaults.Duration > TimeSpan.Zero);
        Assert.True(defaults.SampleInterval >= TimeSpan.FromSeconds(1));
        Assert.True(defaults.RttMs >= 0);
    }

    /// <summary>عدّاد الموارد يعطي رقمًا معقولًا على هذا النظام، وإلا كان «لا نمو» جملة بلا مصدر.</summary>
    [Fact]
    public void ProcessProbe_ReadsSomethingOnThisPlatform()
    {
        Assert.True(ProcessProbe.Settled() > 0);
        Assert.True(ProcessProbe.ThreadCount() > 0);
        Assert.True(ProcessProbe.OpenHandles() > 0, "no handle/fd source on this platform: the soak's handle trend would be blind");
    }
}
