using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace RouteBridge.Infrastructure.Settings;

public interface IAppSettingsStore
{
    /// <summary>The current settings (loaded once from disk; defaults when the file is missing or unreadable).</summary>
    AppSettings Current { get; }

    /// <summary>Raised after a successful <see cref="SaveAsync"/>, possibly on a background thread.</summary>
    event EventHandler<AppSettings>? Changed;

    /// <summary>Atomically writes the file (temp file + rename) and updates <see cref="Current"/>.</summary>
    Task SaveAsync(AppSettings settings, CancellationToken ct);
}

/// <summary><see cref="IAppSettingsStore"/> over a JSON file (camelCase keys, lowercase enum values, indented for hand edits).</summary>
public sealed class AppSettingsStore : IAppSettingsStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.General)
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    private readonly string _path;
    private readonly ILogger<AppSettingsStore> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Lazy<AppSettings> _initial;
    private AppSettings? _current;

    /// <summary>Store at <see cref="AppPaths.SettingsFile"/>.</summary>
    public AppSettingsStore(ILogger<AppSettingsStore>? logger = null)
        : this(AppPaths.SettingsFile, logger)
    {
    }

    /// <summary>Store at an explicit file path (tests).</summary>
    public AppSettingsStore(string path, ILogger<AppSettingsStore>? logger = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = Path.GetFullPath(path);
        _logger = logger ?? NullLogger<AppSettingsStore>.Instance;
        _initial = new Lazy<AppSettings>(Load, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public string FilePath => _path;

    public AppSettings Current => Volatile.Read(ref _current) ?? _initial.Value;

    public event EventHandler<AppSettings>? Changed;

    public async Task SaveAsync(AppSettings settings, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(settings);

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var tmp = _path + ".tmp";
            var bytes = JsonSerializer.SerializeToUtf8Bytes(settings, Json);
            await File.WriteAllBytesAsync(tmp, bytes, ct).ConfigureAwait(false);
            File.Move(tmp, _path, overwrite: true);
            Volatile.Write(ref _current, settings);
            _logger.LogInformation("Settings saved to {Path}", _path);
        }
        finally
        {
            _gate.Release();
        }

        Changed?.Invoke(this, settings);
    }

    private AppSettings Load()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return AppSettings.Default;
            }

            using var stream = File.OpenRead(_path);
            var loaded = JsonSerializer.Deserialize<AppSettings>(stream, Json);
            if (loaded is null)
            {
                _logger.LogWarning("Settings file {Path} is empty; using defaults", _path);
                return AppSettings.Default;
            }

            return loaded with { ServerUrl = loaded.ServerUrl?.Trim() ?? string.Empty };
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Settings file {Path} could not be read; using defaults", _path);
            return AppSettings.Default;
        }
    }
}
