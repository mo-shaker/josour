using System.Text.Json;

namespace RouteBridge.Spike;

/// <summary>
/// سجل الأحداث المزدوج لأمر <c>session</c>: سطر JSON واحد لكل حدث على <b>stdout</b> (يقرأه سكربت QA أو يُحفظ كـ
/// ‏JSONL)، وسطر بشري على <b>stderr</b> (يقرأه الإنسان الذي يشغّل الأداة). فصلهما يعني أن
/// <c>… &gt; run.jsonl</c> يعطي ملفًا صالحًا للتحليل بينما تبقى المتابعة الحية على الشاشة.
///
/// <para>شكل كل سطر: <c>{"ts","seq","role","event", …حقول الحدث}</c>. <c>ts</c> بـ ISO-8601 UTC و<c>seq</c> يبدأ من 1.</para>
/// </summary>
public sealed class EventLog
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = false };
    private readonly object _gate = new();
    private readonly string _role;
    private int _seq;

    public EventLog(string role) => _role = role;

    /// <summary>هل تُطبع الأسطر البشرية على stderr؟ (<c>--quiet</c> يطفئها.)</summary>
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
                // حقول الحدث لا تدهس مفاتيح المظروف.
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

    /// <summary>سطر بشري بلا حدث JSON (تعليمات وتحذيرات لا تخص مجرى البروتوكول).</summary>
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
