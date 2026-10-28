using System.Diagnostics;
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
/// <param name="WrittenAt">When the marker was written: with the PID, what tells one session's marker from the next one's.</param>
public sealed record SystemProxyRunMarker(
    [property: JsonPropertyName("process_id")] int ProcessId,
    [property: JsonPropertyName("process_started_at")] DateTimeOffset ProcessStartedAt,
    [property: JsonPropertyName("port")] int Port,
    [property: JsonPropertyName("snapshot")] SystemProxySnapshot Snapshot,
    [property: JsonPropertyName("written_at")] DateTimeOffset WrittenAt)
{
    /// <summary>The same change in force — the same process, port and moment of writing — whatever the snapshot's encoding.</summary>
    public bool IsSameRunAs(SystemProxyRunMarker? other)
        => other is not null
           && other.ProcessId == ProcessId
           && other.Port == Port
           && other.WrittenAt.UtcTicks == WrittenAt.UtcTicks;
}

public interface ISystemProxyMarkerStore
{
    /// <summary>The marker, or null when there is none — or when the one on disk cannot be read right now (see <see cref="Exists"/>).</summary>
    SystemProxyRunMarker? Read();

    /// <summary>
    /// Is a marker on disk, readable or not? A marker that cannot be read at this moment (another program holds the file)
    /// still means the settings are not the user's own, so this — not <see cref="Read"/> — answers "is a change in force".
    /// </summary>
    bool Exists => Read() is not null;

    /// <summary>False when it could not be written; the settings must then not be changed, as nothing could put them back.</summary>
    bool Write(SystemProxyRunMarker marker);

    void Clear();

    /// <summary>
    /// Removes the marker only while it is still <paramref name="marker"/>'s: a restore that finishes after another process
    /// started a session of its own must not delete that session's marker. False when the marker on disk is another one.
    /// </summary>
    bool ClearIf(SystemProxyRunMarker marker)
    {
        ArgumentNullException.ThrowIfNull(marker);
        var current = Read();
        if (current is null ? Exists : !marker.IsSameRunAs(current)) return false;
        Clear();
        return true;
    }

    /// <summary>
    /// The lock every process holds while it changes the settings or puts them back — the app, its guardian, the restore at
    /// login. Null when it could not be had within <paramref name="timeout"/>.
    /// </summary>
    Task<IDisposable?> LockAsync(TimeSpan timeout, CancellationToken ct) => Task.FromResult<IDisposable?>(NoLock.Instance);

    private sealed class NoLock : IDisposable
    {
        public static NoLock Instance { get; } = new();
        public void Dispose() { }
    }
}

/// <summary><c>%LOCALAPPDATA%\Josour\system-proxy.json</c> (macOS: <c>~/Library/Application Support/Josour</c>).</summary>
public sealed class SystemProxyMarkerStore : ISystemProxyMarkerStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.General) { WriteIndented = true };
    private static readonly TimeSpan LockPoll = TimeSpan.FromMilliseconds(100);
    private const int ReadAttempts = 3;

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

    /// <summary>Where a marker that is not JSON is moved, so it neither blocks every later session nor disappears unseen.</summary>
    public string CorruptFilePath => _path + ".corrupt";

    public bool Exists => File.Exists(_path);

    public SystemProxyRunMarker? Read()
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                if (!File.Exists(_path)) return null;
                // Shared for writing and deleting: a reader must never be the reason the next Write cannot move its file in.
                using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                var marker = stream.Length == 0 ? null : JsonSerializer.Deserialize<SystemProxyRunMarker>(stream, Json);
                if (marker?.Snapshot is null)
                {
                    Quarantine("it is empty or incomplete");
                    return null;
                }

                return marker;
            }
            catch (FileNotFoundException)
            {
                return null;
            }
            catch (JsonException ex)
            {
                Quarantine(ex.Message);
                return null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Usually another program holding the file for a moment (an antivirus scan of a file just written).
                if (attempt < ReadAttempts)
                {
                    Thread.Sleep(50 * attempt);
                    continue;
                }

                _logger.LogWarning(ex, "The system-proxy marker {Path} is there but could not be read; it is kept for a later attempt", _path);
                return null;
            }
        }
    }

    public bool Write(SystemProxyRunMarker marker)
    {
        ArgumentNullException.ThrowIfNull(marker);
        try
        {
            var directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            // Written beside and then moved over, so a crash mid-write never leaves half a marker behind — and flushed to the
            // disk before the move, so a power cut cannot leave a renamed but empty file, which is the case this note exists for.
            var temp = _path + ".tmp";
            using (var stream = new FileStream(temp, OwnerOnly(FileMode.Create, FileAccess.Write)))
            {
                stream.Write(JsonSerializer.SerializeToUtf8Bytes(marker, Json));
                stream.Flush(flushToDisk: true);
            }

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

    public bool ClearIf(SystemProxyRunMarker marker)
    {
        ArgumentNullException.ThrowIfNull(marker);
        var current = Read();
        if (current is not null && !marker.IsSameRunAs(current))
        {
            _logger.LogInformation(
                "The system-proxy marker now belongs to another session (process {Pid}, port {Port}); it is left in place",
                current.ProcessId,
                current.Port);
            return false;
        }

        if (current is null && Exists)
        {
            return false; // there but unreadable: not known to be ours, so not ours to delete
        }

        Clear();
        return true;
    }

    /// <summary>
    /// An exclusive handle on <c>system-proxy.json.lock</c>. <see cref="FileShare.None"/> is a real lock between processes on
    /// both systems (a share mode on Windows, <c>flock</c> on macOS), and it goes away with the process that held it.
    /// </summary>
    public async Task<IDisposable?> LockAsync(TimeSpan timeout, CancellationToken ct)
    {
        var lockPath = _path + ".lock";
        var waited = Stopwatch.StartNew();
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var directory = Path.GetDirectoryName(lockPath);
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
                return new FileStream(lockPath, OwnerOnly(FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None));
            }
            catch (UnauthorizedAccessException ex)
            {
                // A lock that can never be taken must not stop the settings from ever being put back.
                _logger.LogWarning(ex, "The system-proxy lock {Path} cannot be created; continuing without it", lockPath);
                return NotLocked.Instance;
            }
            catch (IOException) when (waited.Elapsed < timeout)
            {
                await Task.Delay(LockPoll, ct).ConfigureAwait(false);
            }
            catch (IOException ex)
            {
                _logger.LogWarning(ex, "The system-proxy lock {Path} is held by another process (waited {Waited:0.#} s)", lockPath, waited.Elapsed.TotalSeconds);
                return null;
            }
        }
    }

    private void Quarantine(string why)
    {
        _logger.LogError(
            "The system-proxy marker {Path} is not a marker ({Why}); it is moved to {Corrupt}. If the internet does not work, use \"Repair internet settings\" or check the proxy settings by hand",
            _path,
            why,
            CorruptFilePath);
        try
        {
            File.Move(_path, CorruptFilePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "The unreadable system-proxy marker {Path} could not be moved aside", _path);
        }
    }

    private sealed class NotLocked : IDisposable
    {
        public static NotLocked Instance { get; } = new();
        public void Dispose() { }
    }

    /// <summary>A new file is created readable by its owner alone (Windows has no such mode; the folder's ACL covers it).</summary>
    private static FileStreamOptions OwnerOnly(FileMode mode, FileAccess access, FileShare share = FileShare.None)
    {
        var options = new FileStreamOptions { Mode = mode, Access = access, Share = share };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        return options;
    }
}
