namespace Josour.Egress.Tests;

public class StreamLimiterTests
{
    [Fact]
    public void ConcurrentCap_256_ThenLimit_ThenReleaseFreesSlot()
    {
        var limiter = new StreamLimiter(maxConcurrent: 256, maxOpensPerSecond: 10_000);
        var leases = new List<StreamLimiter.Lease>();
        for (var i = 0; i < 256; i++)
        {
            var lease = limiter.TryAcquire();
            Assert.NotNull(lease);
            leases.Add(lease!);
        }
        Assert.Equal(256, limiter.Active);
        Assert.Null(limiter.TryAcquire());

        leases[0].Dispose();
        Assert.Equal(255, limiter.Active);
        Assert.NotNull(limiter.TryAcquire());
        Assert.Null(limiter.TryAcquire());
    }

    [Fact]
    public void RateCap_50PerSecond_SlidingWindow()
    {
        long now = 1_000_000;
        var limiter = new StreamLimiter(maxConcurrent: 10_000, maxOpensPerSecond: 50, nowTicks: () => now);
        for (var i = 0; i < 50; i++)
        {
            Assert.NotNull(limiter.TryAcquire()); // one open per millisecond: t0 .. t0+49
            now++;
        }
        now = 1_000_000 + 999;
        Assert.Null(limiter.TryAcquire());
        now = 1_000_000 + 1000;
        Assert.NotNull(limiter.TryAcquire()); // the oldest open (t0) left the window; the other 49 are still inside it
        Assert.Null(limiter.TryAcquire());
    }

    [Fact]
    public void ReleasingConcurrentSlot_DoesNotRefundRate()
    {
        long now = 0;
        var limiter = new StreamLimiter(maxConcurrent: 10, maxOpensPerSecond: 2, nowTicks: () => now);
        var a = limiter.TryAcquire();
        var b = limiter.TryAcquire();
        Assert.NotNull(a);
        Assert.NotNull(b);
        a!.Dispose();
        b!.Dispose();
        Assert.Null(limiter.TryAcquire());
    }

    [Fact]
    public void Lease_DisposeIsIdempotent()
    {
        var limiter = new StreamLimiter(maxConcurrent: 1);
        var lease = limiter.TryAcquire()!;
        lease.Dispose();
        lease.Dispose();
        Assert.Equal(0, limiter.Active);
    }

    [Fact]
    public void Defaults_MatchProtocol()
    {
        var limiter = new StreamLimiter();
        Assert.Equal(256, limiter.MaxConcurrent);
        Assert.Equal(50, limiter.MaxOpensPerSecond);
    }
}
