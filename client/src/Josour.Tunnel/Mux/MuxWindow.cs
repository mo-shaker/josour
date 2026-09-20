using System.Globalization;

namespace Josour.Tunnel.Mux;

/// <summary>
/// The per-stream receiving window and the concurrent stream limit that goes with it (docs/protocol.md section 5 after the week-5 amendment).
///
/// A single stream's ceiling = the window ÷ the RTT exactly (the measurements in <c>docs/performance-week5.md</c>: 99% to 102% efficiency),
/// so the window is derived from the RTT measured at connect time (<c>connect_ms</c>) to stay above 110 Mbit/s up to 300 ms:
///
/// <code>
/// | Measured RTT | Window | Concurrency | The resulting ceiling  |
/// | <= 60 ms     | 1 MiB  | 256         | >= 140 Mbit/s          |
/// | <= 150 ms    | 2 MiB  | 128         | >= 110 Mbit/s at 150 ms|
/// | &gt; 150 ms     | 4 MiB  |  64         | >= 110 Mbit/s at 300 ms|
/// </code>
///
/// <b>Why the limit falls as the window grows:</b> the window is credit rather than a reservation, but the theoretical worst case (every stream
/// stalled with its window full) is the concurrency limit x the window. 256 x 4 MiB = 1 GiB on what may be a personal computer,
/// so the product is pinned at <see cref="MemoryBudget"/> = 256 MiB in every band.
///
/// <b>Why this needs no negotiation on the wire:</b> in Nerdbank protocol 3 the receiving window is the <i>receiver's</i> property
/// and the offer/accept frame carries it per channel: <c>Channel.localWindowSize</c> = what we announced, and <c>remoteWindowSize</c>
/// (the sending credit) = what the other side announced. So each side sizes its own receiving window, and the two sides being in different bands
/// is legal: each direction is governed by its own receiver's window.
/// </summary>
public sealed record MuxWindow
{
    /// <summary>The lowest band: 1 MiB (the only window before the week-5 amendment).</summary>
    public const int NearWindow = 1024 * 1024;

    /// <summary>The middle band: 2 MiB. It is also the fallback when <c>connect_ms</c> is implausible.</summary>
    public const int RegionalWindow = 2 * 1024 * 1024;

    /// <summary>The highest band: 4 MiB.</summary>
    public const int DistantWindow = 4 * 1024 * 1024;

    /// <summary>The memory budget per session: the window x the concurrency limit (docs/protocol.md section 5).</summary>
    public const int MemoryBudget = 256 * 1024 * 1024;

    /// <summary>The upper bound on concurrent streams however small the window (the contract's original value).</summary>
    public const int MaxConcurrentStreamsCap = 256;

    /// <summary>The stream limit at the default window; the compatibility value for whoever derives nothing.</summary>
    public const int DefaultMaxConcurrentStreams = MaxConcurrentStreamsCap;

    /// <summary>The highest RTT in the lowest band.</summary>
    public static readonly TimeSpan NearRoundTrip = TimeSpan.FromMilliseconds(60);

    /// <summary>The highest RTT in the middle band.</summary>
    public static readonly TimeSpan RegionalRoundTrip = TimeSpan.FromMilliseconds(150);

    /// <summary>Above this the number is not a round-trip time but a corrupt measurement (a timeout, a clock that jumped, a missing field).</summary>
    public static readonly TimeSpan MaxPlausibleRoundTrip = TimeSpan.FromSeconds(5);

    private MuxWindow(int receiveWindow, int maxConcurrentStreams, bool isFallback, string reason)
    {
        ReceiveWindow = receiveWindow;
        MaxConcurrentStreams = maxConcurrentStreams;
        IsFallback = isFallback;
        Reason = reason;
    }

    /// <summary>The per-stream receiving window in bytes (<c>DefaultChannelReceivingWindowSize</c> in Nerdbank).</summary>
    public int ReceiveWindow { get; }

    /// <summary>The maximum number of concurrent streams that keeps the product within <see cref="MemoryBudget"/>.</summary>
    public int MaxConcurrentStreams { get; }

    /// <summary>Did it come from the fallback because the RTT given was not a plausible number?</summary>
    public bool IsFallback { get; }

    /// <summary>One line explaining the choice (recorded in the session's diagnostics; with no secrets and no payloads).</summary>
    public string Reason { get; }

    /// <summary>The theoretical ceiling for one stream at this RTT (bytes/second) = the window ÷ the RTT.</summary>
    public double CeilingBytesPerSecond(TimeSpan roundTrip)
        => roundTrip > TimeSpan.Zero ? ReceiveWindow / roundTrip.TotalSeconds : double.PositiveInfinity;

    /// <summary>What the worst case consumes: every stream stalled with its window full.</summary>
    public long WorstCaseBytes => (long)ReceiveWindow * MaxConcurrentStreams;

    /// <summary>The default window with no derivation: 1 MiB and 256 streams (the pre-amendment behaviour, literally).</summary>
    public static MuxWindow Default { get; } = new(NearWindow, LimitFor(NearWindow), false, "default window (no measured round trip)");

    /// <summary>
    /// The binding derivation in docs/protocol.md section 5. It never throws: an implausible number (zero, negative, or greater than
    /// <see cref="MaxPlausibleRoundTrip"/>) gives the middle band with a <see cref="Reason"/> explaining why,
    /// because choosing a window from a corrupt measurement is worse than choosing the middle.
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
    /// A window given explicitly (the <c>MuxOptions.ReceiveWindow</c> override in the tests and the Spike tool). The concurrency limit follows it
    /// under the same budget unless it too is given explicitly.
    /// </summary>
    public static MuxWindow ForWindow(int receiveWindow, int? maxConcurrentStreams = null, string? reason = null)
    {
        if (receiveWindow < 1) throw new ArgumentOutOfRangeException(nameof(receiveWindow), receiveWindow, "receive window must be positive");
        if (maxConcurrentStreams is < 1) throw new ArgumentOutOfRangeException(nameof(maxConcurrentStreams), maxConcurrentStreams, "stream limit must be positive");
        var limit = maxConcurrentStreams ?? LimitFor(receiveWindow);
        return new MuxWindow(receiveWindow, limit, false, reason ?? $"explicit window {Describe(receiveWindow, limit)}");
    }

    /// <summary>Replaces the concurrency limit alone (the <c>MuxOptions.MaxConcurrentStreams</c> override) and keeps the window's reason.</summary>
    public MuxWindow WithMaxConcurrentStreams(int maxConcurrentStreams)
    {
        if (maxConcurrentStreams < 1) throw new ArgumentOutOfRangeException(nameof(maxConcurrentStreams), maxConcurrentStreams, "stream limit must be positive");
        return maxConcurrentStreams == MaxConcurrentStreams
            ? this
            : new MuxWindow(ReceiveWindow, maxConcurrentStreams, IsFallback, $"{Reason}; stream limit overridden to {maxConcurrentStreams}");
    }

    public override string ToString() => Describe(ReceiveWindow, MaxConcurrentStreams);

    /// <summary>The concurrency limit that keeps the window x the limit within the budget, capped at 256 (the contract's original value).</summary>
    private static int LimitFor(int receiveWindow) => Math.Clamp(MemoryBudget / receiveWindow, 1, MaxConcurrentStreamsCap);

    private static string Describe(int receiveWindow) => Describe(receiveWindow, LimitFor(receiveWindow));

    private static string Describe(int receiveWindow, int limit)
        => string.Create(CultureInfo.InvariantCulture, $"{receiveWindow / 1024.0 / 1024.0:0.###} MiB x {limit} streams");

    private static string Ms(TimeSpan value) => value.TotalMilliseconds.ToString("0.###", CultureInfo.InvariantCulture);
}
