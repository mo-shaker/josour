namespace RouteBridge.Egress;

/// <summary>عدّاد البايتات في الاتجاهين على المضيف: Up = من Guest إلى الموقع، Down = من الموقع إلى Guest. آمن للخيوط.</summary>
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
