using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Josour.Core.Session;

namespace Josour.Infrastructure.Settings;

/// <summary>The host's auto-accept switch and trusted-guest rules (docs/ws-protocol.md section 5a).</summary>
public interface IAutoAcceptStore
{
    /// <summary>The current configuration; <see cref="AutoAcceptSettings.Default"/> — switch off — when the file is
    /// missing or cannot be read.</summary>
    AutoAcceptSettings Current { get; }

    event EventHandler<AutoAcceptSettings>? Changed;

    /// <summary>Atomically writes the file (temp file + rename) and updates <see cref="Current"/>.</summary>
    Task SaveAsync(AutoAcceptSettings settings, CancellationToken ct);
}

/// <summary>
/// <see cref="IAutoAcceptStore"/> over <see cref="AppPaths.AutoAcceptFile"/>.
/// <para>
/// A file that cannot be read becomes <see cref="AutoAcceptSettings.Default"/> — switch off — and is logged loudly.
/// That is the only safe direction to fail in: a corrupt file must never be read as "accept everybody", and a host
/// that finds auto-accept unexpectedly off will notice and turn it back on, which is the harmless half of being wrong.
/// </para>
/// </summary>
public sealed class AutoAcceptStore : IAutoAcceptStore
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
    private readonly ILogger<AutoAcceptStore> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Lazy<AutoAcceptSettings> _initial;
    private AutoAcceptSettings? _current;

    public AutoAcceptStore(ILogger<AutoAcceptStore>? logger = null)
        : this(AppPaths.AutoAcceptFile, logger)
    {
    }

    /// <summary>Store at an explicit file path (tests).</summary>
    public AutoAcceptStore(string path, ILogger<AutoAcceptStore>? logger = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = Path.GetFullPath(path);
        _logger = logger ?? NullLogger<AutoAcceptStore>.Instance;
        _initial = new Lazy<AutoAcceptSettings>(Load, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public string FilePath => _path;

    public AutoAcceptSettings Current => Volatile.Read(ref _current) ?? _initial.Value;

    public event EventHandler<AutoAcceptSettings>? Changed;

    public async Task SaveAsync(AutoAcceptSettings settings, CancellationToken ct)
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
            // The count, never the guests: who a host trusts is not something the log needs to carry around.
            _logger.LogInformation(
                "Auto-accept saved to {Path}: enabled={Enabled}, {Count} trusted guest(s)",
                _path,
                settings.Enabled,
                settings.Guests.Count);
        }
        finally
        {
            _gate.Release();
        }

        Changed?.Invoke(this, settings);
    }

    private AutoAcceptSettings Load()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return AutoAcceptSettings.Default;
            }

            using var stream = File.OpenRead(_path);
            var loaded = JsonSerializer.Deserialize<AutoAcceptSettings>(stream, Json);
            if (loaded is null)
            {
                _logger.LogWarning("Auto-accept file {Path} is empty; auto-accept stays off", _path);
                return AutoAcceptSettings.Default;
            }

            // An explicit "guests": null deserializes to null and would make every accessor below throw — out of a
            // Lazy, which then rethrows on every later read of Current. A hand-edited file must not be able to do that.
            var guests = loaded.Guests ?? Array.Empty<TrustedGuest>();

            // A rule with no identity cannot be matched (AutoAcceptPolicy refuses Guid.Empty anyway), and a null entry
            // is JSON noise. Either would only sit in the settings list looking like a decision the host had made.
            var usable = guests.Where(g => g is { HasUsableIdentity: true }).ToArray();
            if (usable.Length != guests.Count)
            {
                _logger.LogWarning(
                    "Auto-accept file {Path}: dropped {Count} trusted-guest entr(ies) with no usable identity",
                    _path,
                    guests.Count - usable.Length);
            }

            return loaded with { Guests = usable };
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // Deliberately not a silent default: this file is a consent record, and losing it should be visible.
            _logger.LogError(ex, "Auto-accept file {Path} could not be read; auto-accept stays off", _path);
            return AutoAcceptSettings.Default;
        }
    }
}
