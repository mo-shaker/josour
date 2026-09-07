using Josour.Tunnel.Mux;

namespace Josour.Tunnel.Tests.Mux;

/// <summary>
/// جدول الشرائح في docs/protocol.md القسم 5 عند حدوده بالضبط، والملاذ عند قياس فاسد، وميزانية الذاكرة.
/// دالة صرفة بلا مقابس، فهي أسرع اختبارات هذه التجميعة.
/// </summary>
public class MuxWindowTests
{
    private const int MiB = 1024 * 1024;

    // الحدود حرفيًا: ≤ 60 ms ⇒ 1 MiB/256، ≤ 150 ms ⇒ 2 MiB/128، فوقها ⇒ 4 MiB/64.
    [Theory]
    [InlineData(0.001, 1, 256)]
    [InlineData(1, 1, 256)]
    [InlineData(20, 1, 256)]
    [InlineData(59.999, 1, 256)]
    [InlineData(60, 1, 256)]          // الحد الأعلى للشريحة الدنيا داخلها
    [InlineData(60.001, 2, 128)]      // وأول ما بعده في الوسطى
    [InlineData(61, 2, 128)]
    [InlineData(150, 2, 128)]         // الحد الأعلى للوسطى داخلها
    [InlineData(150.001, 4, 64)]      // وأول ما بعده في العليا
    [InlineData(200, 4, 64)]
    [InlineData(300, 4, 64)]
    [InlineData(5000, 4, 64)]         // آخر رقم معقول
    public void ForRoundTrip_PicksTierAtExactBoundaries(double rttMs, int expectedMiB, int expectedStreams)
    {
        var window = MuxWindow.ForRoundTrip(TimeSpan.FromMilliseconds(rttMs));

        Assert.Equal(expectedMiB * MiB, window.ReceiveWindow);
        Assert.Equal(expectedStreams, window.MaxConcurrentStreams);
        Assert.False(window.IsFallback);
        Assert.Contains(rttMs.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture), window.Reason);
    }

    // قياس فاسد (حقل مفقود، ساعة قفزت، مهلة اتصال طويلة) لا يختار نافذة من قمامة ولا يرمي: الشريحة الوسطى وسبب مكتوب.
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-300)]
    [InlineData(-5_000_000)]
    [InlineData(5000.001)]
    [InlineData(30_000)]
    [InlineData(2_147_483_647.0)] // connect_ms عند أقصى int
    public void ForRoundTrip_WithImplausibleValue_FallsBackToMiddleTier_AndSaysWhy(double rttMs)
    {
        var window = MuxWindow.ForRoundTrip(TimeSpan.FromMilliseconds(rttMs));

        Assert.Equal(2 * MiB, window.ReceiveWindow);
        Assert.Equal(128, window.MaxConcurrentStreams);
        Assert.True(window.IsFallback);
        Assert.Contains("not plausible", window.Reason);
        Assert.Contains("middle tier", window.Reason);
    }

    [Theory]
    [InlineData(long.MinValue + 1)]
    [InlineData(long.MaxValue)]
    public void ForRoundTrip_WithExtremeTimeSpan_DoesNotThrow(long ticks)
    {
        var window = MuxWindow.ForRoundTrip(TimeSpan.FromTicks(ticks));

        Assert.Equal(2 * MiB, window.ReceiveWindow);
        Assert.True(window.IsFallback);
    }

    // سبب الاختيار يُسجَّل في تشخيص الجلسة، فلا يجوز أن يكون فارغًا في أي مسار.
    [Theory]
    [InlineData(10)]
    [InlineData(100)]
    [InlineData(400)]
    [InlineData(0)]
    public void Reason_IsAlwaysLoggable(double rttMs)
    {
        var reason = MuxWindow.ForRoundTrip(TimeSpan.FromMilliseconds(rttMs)).Reason;
        Assert.False(string.IsNullOrWhiteSpace(reason));
    }

    // العلة الأصلية للجدول: الحاصل ثابت عند 256 MiB في كل شريحة (docs/protocol.md القسم 5، حد الذاكرة).
    [Theory]
    [InlineData(30)]
    [InlineData(120)]
    [InlineData(300)]
    [InlineData(0)]
    public void EveryTier_HoldsTheSameMemoryBudget(double rttMs)
    {
        var window = MuxWindow.ForRoundTrip(TimeSpan.FromMilliseconds(rttMs));
        Assert.Equal(MuxWindow.MemoryBudget, window.WorstCaseBytes);
    }

    // السقف = النافذة ÷ RTT: هذا هو سبب وجود الجدول أصلًا، ولا شريحة تنزل تحت 110 Mbit/s في مداها.
    [Theory]
    [InlineData(60, 139.8)]  // «≥ 140 Mbit/s» في العقد تقريب لـ 139.81 = 1 MiB ÷ 60 ms
    [InlineData(150, 110)]
    [InlineData(300, 110)]
    public void EveryTier_KeepsThePerStreamCeilingAboveTheTarget(double rttMs, double minMegabits)
    {
        var rtt = TimeSpan.FromMilliseconds(rttMs);
        var megabits = MuxWindow.ForRoundTrip(rtt).CeilingBytesPerSecond(rtt) * 8 / 1_000_000;
        Assert.True(megabits >= minMegabits, $"{rttMs} ms ⇒ {megabits:F1} Mbit/s < {minMegabits}");
    }

    [Theory]
    [InlineData(64 * 1024, 256)]      // نافذة صغيرة لا ترفع السقف فوق 256
    [InlineData(1 * MiB, 256)]
    [InlineData(2 * MiB, 128)]
    [InlineData(4 * MiB, 64)]
    [InlineData(8 * MiB, 32)]
    [InlineData(512 * MiB, 1)]        // نافذة أكبر من الميزانية كلها: stream واحد لا صفر
    public void ForWindow_DerivesTheStreamLimitFromTheBudget(int window, int expectedStreams)
        => Assert.Equal(expectedStreams, MuxWindow.ForWindow(window).MaxConcurrentStreams);

    [Fact]
    public void ForWindow_KeepsAnExplicitStreamLimit()
    {
        var window = MuxWindow.ForWindow(4 * MiB, maxConcurrentStreams: 8);
        Assert.Equal(4 * MiB, window.ReceiveWindow);
        Assert.Equal(8, window.MaxConcurrentStreams);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void ForWindow_RejectsNonPositiveWindow(int window)
        => Assert.Throws<ArgumentOutOfRangeException>(() => MuxWindow.ForWindow(window));

    [Fact]
    public void ForWindow_RejectsNonPositiveStreamLimit()
        => Assert.Throws<ArgumentOutOfRangeException>(() => MuxWindow.ForWindow(MiB, maxConcurrentStreams: 0));

    [Fact]
    public void Default_IsTheContractsPreAmendmentWindow()
    {
        Assert.Equal(MiB, MuxWindow.Default.ReceiveWindow);
        Assert.Equal(256, MuxWindow.Default.MaxConcurrentStreams);
        Assert.False(MuxWindow.Default.IsFallback);
    }

    // ---------- MuxOptions.Resolve: التجاوز الصريح يفوز على الاشتقاق ----------

    [Fact]
    public void Resolve_WithoutOverrides_UsesTheDerivedWindow()
    {
        var derived = MuxWindow.ForRoundTrip(TimeSpan.FromMilliseconds(200));
        var resolved = new MuxOptions().Resolve(derived);
        Assert.Equal(derived, resolved);
    }

    [Fact]
    public void Resolve_WithoutAnything_IsTheDefaultWindow()
        => Assert.Equal(MuxWindow.Default, new MuxOptions().Resolve());

    [Fact]
    public void Resolve_WithExplicitWindow_OverridesTheDerivedOne()
    {
        var resolved = new MuxOptions { ReceiveWindow = 4 * MiB }.Resolve(MuxWindow.ForRoundTrip(TimeSpan.FromMilliseconds(20)));

        Assert.Equal(4 * MiB, resolved.ReceiveWindow);
        Assert.Equal(64, resolved.MaxConcurrentStreams); // الحد يتبع النافذة المفروضة أيضًا
        Assert.Contains("explicit", resolved.Reason);
    }

    [Fact]
    public void Resolve_WithExplicitStreamLimit_KeepsTheDerivedWindow()
    {
        var resolved = new MuxOptions { MaxConcurrentStreams = 4 }.Resolve(MuxWindow.ForRoundTrip(TimeSpan.FromMilliseconds(200)));

        Assert.Equal(4 * MiB, resolved.ReceiveWindow);
        Assert.Equal(4, resolved.MaxConcurrentStreams);
        Assert.Contains("overridden", resolved.Reason);
    }
}
