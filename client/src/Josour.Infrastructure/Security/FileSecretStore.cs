using System.Runtime.Versioning;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Josour.Core.Security;

namespace Josour.Infrastructure.Security;

/// <summary>
/// <see cref="ISecretStore"/> as one file per key, readable and writable by its owner alone (mode 0600 in a 0700
/// directory). This is what macOS uses.
/// <para>
/// It replaced the Keychain, and the reason is worth keeping because it is not obvious. The Keychain binds every
/// item to the code-signing identity of the application that created it. Josour is not signed (ADR-0011), so its
/// identity changes with every build — which means every update would produce a binary locked out of its own
/// secrets, and every user would fail to sign in after upgrading, with <c>errSecInvalidOwnerEdit</c> and no way
/// forward. That is not a bug to fix; it is what an unsigned app and the Keychain mean together.
/// </para>
/// <para>
/// <b>What this protects against, and what it does not.</b> The file mode keeps other users on the machine out.
/// It does not keep out another process running as the same user — but neither does DPAPI on Windows, which this
/// product already ships: its entropy is a constant in the source, so any process running as that user can undo
/// it. The two platforms are level on that. Where they differ is at rest with the disk unlocked: DPAPI encrypts,
/// this does not, and on macOS that gap is covered by FileVault, which is on by default on current hardware.
/// </para>
/// <para>Secret values are never logged; only key names and paths are.</para>
/// </summary>
public sealed partial class FileSecretStore : ISecretStore
{
    private const string FileExtension = ".secret";

    /// <summary>Owner read and write, nothing else — for the files.</summary>
    private const UnixFileMode SecretMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    /// <summary>Owner everything, nothing else — for the directory holding them.</summary>
    private const UnixFileMode DirectoryMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    private readonly string _directory;
    private readonly ILogger<FileSecretStore> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Store at <see cref="AppPaths.SecretsDirectory"/>.</summary>
    public FileSecretStore(ILogger<FileSecretStore>? logger = null)
        : this(AppPaths.SecretsDirectory, logger)
    {
    }

    /// <summary>Store at an explicit directory (tests).</summary>
    public FileSecretStore(string directory, ILogger<FileSecretStore>? logger = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        _directory = Path.GetFullPath(directory);
        _logger = logger ?? NullLogger<FileSecretStore>.Instance;
    }

    public string Directory => _directory;

    public async Task<string?> GetAsync(string key, CancellationToken ct)
    {
        var path = PathFor(key);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            // A file that became readable by others — restored from a backup, copied with the wrong tool — is
            // tightened rather than refused. Refusing would lock the user out of their own account to protect
            // them from a permission bit, and the value is about to be re-read either way.
            TightenIfNeeded(path);
            return Encoding.UTF8.GetString(await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Secret {Key} could not be read", key);
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SetAsync(string key, string value, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(value);
        var path = PathFor(key);

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            CreateDirectory();

            // Written to a temp file and moved, so a crash never leaves half a secret. The mode is set on the
            // temp file BEFORE the bytes go in: creating it world-readable and narrowing afterwards would leave
            // a window, however short, where the token is readable by anyone on the machine.
            var tmp = path + ".tmp";
            using (var stream = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                SetMode(tmp, SecretMode);
                await stream.WriteAsync(Encoding.UTF8.GetBytes(value), ct).ConfigureAwait(false);
            }

            File.Move(tmp, path, overwrite: true);
            SetMode(path, SecretMode);
            _logger.LogDebug("Secret {Key} stored", key);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RemoveAsync(string key, CancellationToken ct)
    {
        var path = PathFor(key);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
                _logger.LogDebug("Secret {Key} removed", key);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Secret {Key} could not be removed", key);
        }
        finally
        {
            _gate.Release();
        }
    }

    private void CreateDirectory()
    {
        System.IO.Directory.CreateDirectory(_directory);
        SetMode(_directory, DirectoryMode);
    }

    /// <summary>A no-op on Windows, where the mode does not exist and DPAPI is used instead.</summary>
    private void SetMode(string path, UnixFileMode mode)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            SetUnixMode(path, mode);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            // Worth saying loudly: the secret is stored, but not only for this user.
            _logger.LogError(ex, "Could not restrict the permissions of {Path}; it may be readable by other users", path);
        }
    }

    [UnsupportedOSPlatform("windows")]
    private static void SetUnixMode(string path, UnixFileMode mode) => File.SetUnixFileMode(path, mode);

    private void TightenIfNeeded(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            if (GetUnixMode(path) != SecretMode)
            {
                _logger.LogWarning("Secret file {Path} was readable beyond its owner; tightening it", path);
                SetUnixMode(path, SecretMode);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            _logger.LogWarning(ex, "Could not check the permissions of {Path}", path);
        }
    }

    [UnsupportedOSPlatform("windows")]
    private static UnixFileMode GetUnixMode(string path) => File.GetUnixFileMode(path);

    private string PathFor(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        if (!KeyPattern().IsMatch(key))
        {
            throw new ArgumentException("Secret key must match [a-z0-9_.-]+", nameof(key));
        }

        return Path.Combine(_directory, key + FileExtension);
    }

    /// <summary>Character for character the pattern <see cref="DpapiSecretStore"/> uses: one key set, not two.</summary>
    [GeneratedRegex("^[a-z0-9_.-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex KeyPattern();
}
