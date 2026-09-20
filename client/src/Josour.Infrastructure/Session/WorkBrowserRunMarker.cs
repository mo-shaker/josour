using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Josour.Infrastructure.Session;

/// <summary>
/// The note the app leaves on disk while a work browser is running, so the NEXT start can tell that the last one did not
/// end cleanly (plan 8.5: the application crashing) and close a browser that is still pointing at a proxy which no longer exists.
/// <para>
/// A crash normally takes the browser with it — the launcher's Job Object is <c>KILL_ON_JOB_CLOSE</c> — but that is a
/// fail-safe of the same process. It does not cover a Job that was broken by a policy, a process killed with the Job
/// handle inherited elsewhere, or a browser that handed itself off to an existing instance. The marker turns those into a
/// deterministic clean-up at start-up instead of a browser that keeps offering an allow-listed-looking window.
/// </para>
/// </summary>
/// <param name="ProcessId">The browser process the launcher started.</param>
/// <param name="ProcessName">Its process name (<c>chrome</c> / <c>msedge</c>), checked so a reused PID is not killed.</param>
/// <param name="StartedAtUtc">When that process started; the second half of the anti-PID-reuse check.</param>
/// <param name="ProfileDirectory">The <c>--user-data-dir</c> of the work profile, whose stale lock files are cleaned too.</param>
/// <param name="WrittenAtUtc">When the marker was written (for the log line only).</param>
public sealed record WorkBrowserRunMarker(
    [property: JsonPropertyName("process_id")] int ProcessId,
    [property: JsonPropertyName("process_name")] string ProcessName,
    [property: JsonPropertyName("started_at")] DateTimeOffset StartedAtUtc,
    [property: JsonPropertyName("profile_directory")] string ProfileDirectory,
    [property: JsonPropertyName("written_at")] DateTimeOffset WrittenAtUtc);

/// <summary>Where the marker is kept. One marker at a time: the app runs at most one work browser.</summary>
public interface IWorkBrowserRunMarkerStore
{
    /// <summary>The marker of the last launch, or null when the last run cleaned up after itself.</summary>
    WorkBrowserRunMarker? Read();

    /// <summary>Records a launched browser. Replaces any earlier marker.</summary>
    void Write(WorkBrowserRunMarker marker);

    /// <summary>Removes the marker: the browser is gone and nothing has to be cleaned up next time.</summary>
    void Clear();
}

/// <summary>
/// <see cref="IWorkBrowserRunMarkerStore"/> as one small JSON file next to the settings
/// (<c>%LOCALAPPDATA%\Josour\work-browser.json</c>). Never throws: losing the marker costs a clean-up, not a session.
/// </summary>
public sealed class WorkBrowserRunMarkerStore : IWorkBrowserRunMarkerStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.General) { WriteIndented = true };

    private readonly string _path;
    private readonly ILogger _logger;

    public WorkBrowserRunMarkerStore(ILogger<WorkBrowserRunMarkerStore>? logger = null)
        : this(AppPaths.WorkBrowserMarkerFile, logger)
    {
    }

    public WorkBrowserRunMarkerStore(string path, ILogger<WorkBrowserRunMarkerStore>? logger = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = Path.GetFullPath(path);
        _logger = (ILogger?)logger ?? NullLogger.Instance;
    }

    public string FilePath => _path;

    public WorkBrowserRunMarker? Read()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return null;
            }

            using var stream = File.OpenRead(_path);
            return JsonSerializer.Deserialize<WorkBrowserRunMarker>(stream, Json);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "The work-browser marker {Path} could not be read; treating it as absent", _path);
            return null;
        }
    }

    public void Write(WorkBrowserRunMarker marker)
    {
        ArgumentNullException.ThrowIfNull(marker);
        try
        {
            var directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllBytes(_path, JsonSerializer.SerializeToUtf8Bytes(marker, Json));
            _logger.LogDebug("Work-browser marker written for {ProcessName} ({ProcessId})", marker.ProcessName, marker.ProcessId);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "The work-browser marker {Path} could not be written", _path);
        }
    }

    public void Clear()
    {
        try
        {
            if (File.Exists(_path))
            {
                File.Delete(_path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "The work-browser marker {Path} could not be removed", _path);
        }
    }
}
