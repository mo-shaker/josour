using System.Text.Json;

namespace Josour.Spike;

/// <summary>
/// The dual event log for the <c>session</c> command: one JSON line per event on <b>stdout</b> (read by a QA script or kept as
/// JSONL), and a human line on <b>stderr</b> (read by the person running the tool). Separating them means
/// <c>… &gt; run.jsonl</c> gives a file fit for analysis while the live view stays on the screen.
///
/// <para>Each line's shape: <c>{"ts","seq","role","event", …the event's fields}</c>. <c>ts</c> is ISO-8601 UTC and <c>seq</c> starts at 1.</para>
/// </summary>
public sealed class EventLog
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = false };
    private readonly object _gate = new();
    private readonly string _role;
    private int _seq;

    public EventLog(string role) => _role = role;

    /// <summary>Are the human lines printed on stderr? (<c>--quiet</c> turns them off.)</summary>
    public bool Human { get; init; } = true;

    public void Emit(string name, IReadOnlyDictionary<string, object?>? fields = null, string? human = null)
    {
        var record = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["ts"] = DateTimeOffset.UtcNow.ToString("O"),
            ["seq"] = 0,
            ["role"] = _role,
            ["event"] = name,
        };
        if (fields is not null)
        {
            foreach (var (key, value) in fields)
            {
                // An event's fields do not trample the envelope's keys.
                record[key is "ts" or "seq" or "role" or "event" ? "_" + key : key] = value;
            }
        }

        lock (_gate)
        {
            record["seq"] = ++_seq;
            Console.Out.WriteLine(JsonSerializer.Serialize(record, Options));
            Console.Out.Flush();
            if (Human && human is not null)
            {
                Console.Error.WriteLine($"[{_role}] {human}");
                Console.Error.Flush();
            }
        }
    }

    /// <summary>A human line with no JSON event (instructions and warnings outside the protocol's stream).</summary>
    public void Note(string text)
    {
        if (!Human) return;
        lock (_gate)
        {
            Console.Error.WriteLine($"[{_role}] {text}");
            Console.Error.Flush();
        }
    }

    public static Dictionary<string, object?> Fields(params (string Key, object? Value)[] pairs)
    {
        var map = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (key, value) in pairs) map[key] = value;
        return map;
    }
}
