namespace Josour.Egress;

/// <summary>The byte counter in both directions on the host: Up = from the guest to the site, Down = from the site to the guest. Thread-safe.</summary>
public sealed class ByteCounter
{
    private long _up;
    private long _down;

    public long Up => Volatile.Read(ref _up);
    public long Down => Volatile.Read(ref _down);

    public void AddUp(long bytes) => Interlocked.Add(ref _up, bytes);
    public void AddDown(long bytes) => Interlocked.Add(ref _down, bytes);

    public (long Up, long Down) Snapshot() => (Up, Down);
}
