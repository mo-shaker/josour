using Microsoft.Extensions.Logging;

namespace Josour.Infrastructure.Tests.Support;

/// <summary>
/// Captures every log line (rendered message plus the structured values) so a test can assert what is — and above all what is
/// never — written to the log; the session secret must not appear in any of them.
/// </summary>
public sealed class RecordingLogger<T> : ILogger<T>
{
    private readonly List<string> _lines = new();

    public IReadOnlyList<string> Lines
    {
        get
        {
            lock (_lines)
            {
                return _lines.ToList();
            }
        }
    }

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        ArgumentNullException.ThrowIfNull(formatter);
        var line = formatter(state, exception);
        if (state is IReadOnlyList<KeyValuePair<string, object?>> values)
        {
            line += " | " + string.Join(" ", values.Select(v => $"{v.Key}={v.Value}"));
        }

        if (exception is not null)
        {
            line += " | " + exception;
        }

        lock (_lines)
        {
            _lines.Add(line);
        }
    }

    /// <summary>True when any captured line contains the text (ordinal, case-insensitive).</summary>
    public bool Contains(string text) => Lines.Any(l => l.Contains(text, StringComparison.OrdinalIgnoreCase));
}
