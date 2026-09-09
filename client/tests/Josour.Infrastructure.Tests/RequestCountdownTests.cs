using Josour.Infrastructure.Session;

namespace Josour.Infrastructure.Tests;

/// <summary>
/// The bug these pin was reported as "the Accept button is greyed out" and reproduced as a clock: one
/// host's machine ran ahead of the server, so every incoming request arrived past its own deadline and
/// the window disabled both buttons before it was even shown. The same request answered normally in the
/// other direction.
/// </summary>
public class RequestCountdownTests
{
    private static readonly DateTimeOffset ServerNow = new(2026, 9, 9, 16, 0, 0, TimeSpan.Zero);

    private static RequestCountdown At(DateTimeOffset now, TimeSpan untilExpiry)
        => new(ServerNow + untilExpiry, () => now);

    [Fact]
    public void ANormalRequestCountsDown()
    {
        var countdown = At(ServerNow, TimeSpan.FromSeconds(60));
        Assert.Equal(60, countdown.SecondsRemaining());
        Assert.False(countdown.HasRunOut());
        Assert.False(countdown.LooksSkewed);
    }

    [Fact]
    public void ItRunsOutOnlyAfterHavingBeenOpen()
    {
        var now = ServerNow;
        var countdown = new RequestCountdown(ServerNow + TimeSpan.FromSeconds(2), () => now);
        Assert.False(countdown.HasRunOut());

        now = ServerNow + TimeSpan.FromSeconds(3);
        Assert.Equal(0, countdown.SecondsRemaining());
        Assert.True(countdown.HasRunOut());
    }

    [Fact]
    public void ARequestThatArrivesAlreadyPastItsDeadlineIsNotDeclaredExpired()
    {
        // The whole point. A local clock two minutes fast makes a fresh 60 s request look long dead. The
        // server would not have sent an expired request, so the client must not act on that reading -
        // it shows zero and leaves the host able to answer, and the server's request.expired decides.
        var localClockTwoMinutesFast = ServerNow + TimeSpan.FromMinutes(2);
        var countdown = new RequestCountdown(ServerNow + TimeSpan.FromSeconds(60), () => localClockTwoMinutesFast);

        Assert.Equal(0, countdown.SecondsRemaining());
        Assert.True(countdown.LooksSkewed);
        Assert.False(countdown.HasRunOut(), "a request must never be dead on arrival: that disables both buttons");
    }

    [Fact]
    public void ASkewedCountdownRecoversIfTheClockIsCorrectedMidWindow()
    {
        var now = ServerNow + TimeSpan.FromMinutes(2);
        var countdown = new RequestCountdown(ServerNow + TimeSpan.FromSeconds(60), () => now);
        Assert.True(countdown.LooksSkewed);

        now = ServerNow + TimeSpan.FromSeconds(10); // hello.ack landed and the offset is applied
        Assert.Equal(50, countdown.SecondsRemaining());
        Assert.False(countdown.HasRunOut());
    }

    [Fact]
    public void AClockBehindTheServerDoesNotExtendTheWindowBeyondTheDeadline()
    {
        // The mirror case: a slow clock must not keep the buttons alive forever once the server's
        // deadline has genuinely passed by server time.
        var now = ServerNow;
        var countdown = new RequestCountdown(ServerNow + TimeSpan.FromSeconds(5), () => now);
        Assert.Equal(5, countdown.SecondsRemaining());

        now = ServerNow + TimeSpan.FromSeconds(6);
        Assert.True(countdown.HasRunOut());
    }

    [Fact]
    public void TheClockMustBeSupplied()
        => Assert.Throws<ArgumentNullException>(() => new RequestCountdown(ServerNow, null!));
}
