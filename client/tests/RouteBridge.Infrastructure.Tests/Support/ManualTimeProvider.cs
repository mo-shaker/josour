namespace RouteBridge.Infrastructure.Tests.Support;

/// <summary>A clock the test moves by hand (only <see cref="GetUtcNow"/> is overridden; timers stay real).</summary>
public sealed class ManualTimeProvider : TimeProvider
{
    public DateTimeOffset Now { get; set; } = new(2026, 9, 4, 10, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => Now;
}
