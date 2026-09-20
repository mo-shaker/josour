using Josour.Tunnel.Mux;

namespace Josour.Tunnel.Tests.Mux;

/// <summary>
/// The band table in docs/protocol.md section 5 at its exact boundaries, the fallback on a corrupt measurement, and the memory budget.
/// A pure function with no sockets, so these are the fastest tests in this assembly.
/// </summary>
public class MuxWindowTests
{
    private const int MiB = 1024 * 1024;

    // The boundaries literally: <= 60 ms => 1 MiB/256, <= 150 ms => 2 MiB/128, above that => 4 MiB/64.
    [Theory]
    [InlineData(0.001, 1, 256)]
    [InlineData(1, 1, 256)]
    [InlineData(20, 1, 256)]
    [InlineData(59.999, 1, 256)]
    [InlineData(60, 1, 256)]          // the lowest band's upper boundary, inside it
    [InlineData(60.001, 2, 128)]      // and the first thing past it, in the middle band
    [InlineData(61, 2, 128)]
    [InlineData(150, 2, 128)]         // the middle band's upper boundary, inside it
    [InlineData(150.001, 4, 64)]      // and the first thing past it, in the highest band
    [InlineData(200, 4, 64)]
    [InlineData(300, 4, 64)]
    [InlineData(5000, 4, 64)]         // the last plausible number
    public void ForRoundTrip_PicksTierAtExactBoundaries(double rttMs, int expectedMiB, int expectedStreams)
    {
        var window = MuxWindow.ForRoundTrip(TimeSpan.FromMilliseconds(rttMs));

        Assert.Equal(expectedMiB * MiB, window.ReceiveWindow);
        Assert.Equal(expectedStreams, window.MaxConcurrentStreams);
        Assert.False(window.IsFallback);
        Assert.Contains(rttMs.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture), window.Reason);
    }

    // A corrupt measurement (a missing field, a clock that jumped, a long connect timeout) does not choose a window from rubbish and does not throw: the middle band with a written reason.
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-300)]
    [InlineData(-5_000_000)]
    [InlineData(5000.001)]
    [InlineData(30_000)]
    [InlineData(2_147_483_647.0)] // connect_ms at int's maximum
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

    // The reason for the choice is recorded in the session's diagnostics, so it may not be empty on any path.
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

    // The table's original reason: the product is fixed at 256 MiB in every band (docs/protocol.md section 5, the memory limit).
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

    // The ceiling = the window ÷ the RTT: this is why the table exists at all, and no band falls below 110 Mbit/s within its range.
    [Theory]
    [InlineData(60, 139.8)]  // ">= 140 Mbit/s" in the contract is a rounding of 139.81 = 1 MiB ÷ 60 ms
    [InlineData(150, 110)]
    [InlineData(300, 110)]
    public void EveryTier_KeepsThePerStreamCeilingAboveTheTarget(double rttMs, double minMegabits)
    {
        var rtt = TimeSpan.FromMilliseconds(rttMs);
        var megabits = MuxWindow.ForRoundTrip(rtt).CeilingBytesPerSecond(rtt) * 8 / 1_000_000;
        Assert.True(megabits >= minMegabits, $"{rttMs} ms ⇒ {megabits:F1} Mbit/s < {minMegabits}");
    }

    [Theory]
    [InlineData(64 * 1024, 256)]      // a small window does not raise the ceiling above 256
    [InlineData(1 * MiB, 256)]
    [InlineData(2 * MiB, 128)]
    [InlineData(4 * MiB, 64)]
    [InlineData(8 * MiB, 32)]
    [InlineData(512 * MiB, 1)]        // a window larger than the whole budget: one stream, not zero
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

    // ---------- MuxOptions.Resolve: the explicit override beats the derivation ----------

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
        Assert.Equal(64, resolved.MaxConcurrentStreams); // the limit follows the imposed window too
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
