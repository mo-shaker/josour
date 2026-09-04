namespace RouteBridge.Infrastructure.Control;

/// <summary>Generates the client-side <c>ref</c> of request frames (unique within a connection; docs/ws-protocol.md section 2).</summary>
public static class ControlRef
{
    private static long _counter;

    /// <summary>Short, unique, log-friendly: <c>c12-3f9a</c>.</summary>
    public static string Next()
    {
        var n = Interlocked.Increment(ref _counter);
        return "c" + n.ToString(System.Globalization.CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N")[..4];
    }
}
