using System.Text.Json;
using System.Text.Json.Serialization;
using Josour.Core.Net;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Josour.Infrastructure.SystemProxy;

/// <summary>
/// The note left on disk <b>before</b> the system proxy is changed, holding everything needed to put it back: which process
/// changed it, the port it points at, and the settings as they were. Whoever finds it later — the guardian process when the
/// app dies, the next start, the next login, "Repair internet settings" — restores from it and removes it.
/// </summary>
/// <param name="ProcessId">The app process that applied the settings.</param>
/// <param name="ProcessStartedAt">When that process started (so a reused PID is not mistaken for it).</param>
/// <param name="Port">The local proxy port the system was pointed at.</param>
/// <param name="Snapshot">The settings before the session.</param>
/// <param name="WrittenAt">When the marker was written (for the log only).</param>
public sealed record SystemProxyRunMarker(
    [property: JsonPropertyName("process_id")] int ProcessId,
    [property: JsonPropertyName("process_started_at")] DateTimeOffset ProcessStartedAt,
    [property: JsonPropertyName("port")] int Port,
    [property: JsonPropertyName("snapshot")] SystemProxySnapshot Snapshot,
    [property: JsonPropertyName("written_at")] DateTimeOffset WrittenAt);

public interface ISystemProxyMarkerStore
{
    SystemProxyRunMarker? Read();

    /// <summary>False when it could not be written; the settings must then not be changed, as nothing could put them back.</summary>
    bool Write(SystemProxyRunMarker marker);

    void Clear();
}

/// <summary><c>%LOCALAPPDATA%\Josour\system-proxy.json</c> (macOS: <c>~/Library/Application Support/Josour</c>).</summary>
public sealed class SystemProxyMarkerStore : ISystemProxyMarkerStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.General) { WriteIndented = true };

    private readonly string _path;
    private readonly ILogger _logger;

    public SystemProxyMarkerStore(ILogger<SystemProxyMarkerStore>? logger = null)
        : this(AppPaths.SystemProxyMarkerFile, logger)
    {
    }

    public SystemProxyMarkerStore(string path, ILogger<SystemProxyMarkerStore>? logger = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = Path.GetFullPath(path);
        _logger = (ILogger?)logger ?? NullLogger.Instance;
    }

    public string FilePath => _path;

    public SystemProxyRunMarker? Read()
    {
        try
        {
            if (!File.Exists(_path)) return null;
            using var stream = File.OpenRead(_path);
            return JsonSerializer.Deserialize<SystemProxyRunMarker>(stream, Json);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "The system-proxy marker {Path} could not be read; treating it as absent", _path);
            return null;
        }
    }

    public bool Write(SystemProxyRunMarker marker)
    {
        ArgumentNullException.ThrowIfNull(marker);
        try
        {
            var directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            // Written beside and then moved over, so a crash mid-write never leaves half a marker behind.
            var temp = _path + ".tmp";
            File.WriteAllBytes(temp, JsonSerializer.SerializeToUtf8Bytes(marker, Json));
            File.Move(temp, _path, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "The system-proxy marker {Path} could not be written", _path);
            return false;
        }
    }

    public void Clear()
    {
        try
        {
            if (File.Exists(_path)) File.Delete(_path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "The system-proxy marker {Path} could not be removed", _path);
        }
    }
}
