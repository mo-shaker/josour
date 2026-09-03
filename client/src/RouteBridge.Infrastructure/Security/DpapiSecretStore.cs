using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RouteBridge.Core.Security;

namespace RouteBridge.Infrastructure.Security;

/// <summary>
/// <see cref="ISecretStore"/> backed by Windows DPAPI (<see cref="ProtectedData"/>, current-user scope, app-specific entropy).
/// One file per key under <c>%LOCALAPPDATA%\RouteBridge\secrets\&lt;key&gt;.bin</c>.
/// <para>
/// PRODUCTION IS WINDOWS-ONLY. On non-Windows hosts (developer Macs, CI on Linux) the store falls back to a
/// clearly marked PLAINTEXT file format so that unit tests and UI work can run; that fallback is never
/// shipped to users. A file written by one mode is not readable by the other and is treated as absent.
/// </para>
/// <para>Secret values are never logged; only key names and file paths are.</para>
/// </summary>
public sealed partial class DpapiSecretStore : ISecretStore
{
    private const string FileExtension = ".bin";

    // 8-byte format tags so a file's protection mode is self-describing.
    private static readonly byte[] DpapiMagic = "RBDPAPI1"u8.ToArray();
    private static readonly byte[] PlainMagic = "RBPLAIN1"u8.ToArray();

    // App-specific optional entropy: ties the blob to RouteBridge so another DPAPI consumer running as the same
    // user cannot trivially unprotect it without knowing this constant.
    private static readonly byte[] Entropy = SHA256.HashData("RouteBridge.SecretStore.v1"u8.ToArray());

    private readonly string _directory;
    private readonly ILogger<DpapiSecretStore> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Creates a store rooted at <see cref="AppPaths.SecretsDirectory"/>.</summary>
    public DpapiSecretStore(ILogger<DpapiSecretStore>? logger = null)
        : this(AppPaths.SecretsDirectory, logger)
    {
    }

    /// <summary>Creates a store rooted at an explicit directory (tests).</summary>
    public DpapiSecretStore(string directory, ILogger<DpapiSecretStore>? logger = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        _directory = Path.GetFullPath(directory);
        _logger = logger ?? NullLogger<DpapiSecretStore>.Instance;
    }

    /// <summary>True when secrets are protected by DPAPI (Windows). False means the plaintext development fallback is active.</summary>
    public static bool IsProtectionAvailable => OperatingSystem.IsWindows();

    /// <summary>Directory holding the secret files.</summary>
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

            var blob = await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
            return Decode(key, blob);
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
        var blob = Encode(value);

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            System.IO.Directory.CreateDirectory(_directory);

            // Write to a temp file then move so a crash never leaves a half-written secret.
            var tmp = path + ".tmp";
            await File.WriteAllBytesAsync(tmp, blob, ct).ConfigureAwait(false);
            File.Move(tmp, path, overwrite: true);
            _logger.LogDebug("Secret {Key} stored ({Mode})", key, IsProtectionAvailable ? "dpapi" : "plaintext-dev-fallback");
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
        finally
        {
            _gate.Release();
        }
    }

    // ---------- encoding ----------

    private static byte[] Encode(string value)
    {
        var plaintext = Encoding.UTF8.GetBytes(value);
        try
        {
            if (OperatingSystem.IsWindows())
            {
                return Frame(DpapiMagic, ProtectWindows(plaintext));
            }

            // Non-Windows development fallback: NOT protected. See class remarks.
            return Frame(PlainMagic, plaintext);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    private string? Decode(string key, byte[] blob)
    {
        if (blob.Length < DpapiMagic.Length)
        {
            _logger.LogWarning("Secret {Key}: file too short, treating as absent", key);
            return null;
        }

        var magic = blob.AsSpan(0, DpapiMagic.Length);
        var payload = blob.AsSpan(DpapiMagic.Length).ToArray();

        try
        {
            if (magic.SequenceEqual(DpapiMagic))
            {
                if (!OperatingSystem.IsWindows())
                {
                    _logger.LogWarning("Secret {Key}: DPAPI-protected file cannot be read on this OS, treating as absent", key);
                    return null;
                }

                var plaintext = UnprotectWindows(payload);
                try
                {
                    return Encoding.UTF8.GetString(plaintext);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(plaintext);
                }
            }

            if (magic.SequenceEqual(PlainMagic))
            {
                if (OperatingSystem.IsWindows())
                {
                    // Plaintext files are a development artefact; never trust them in production.
                    _logger.LogWarning("Secret {Key}: plaintext development file found on Windows, ignoring", key);
                    return null;
                }

                return Encoding.UTF8.GetString(payload);
            }

            _logger.LogWarning("Secret {Key}: unknown file format, treating as absent", key);
            return null;
        }
        catch (CryptographicException ex)
        {
            // Typical causes: user profile re-created, file copied from another account, corruption.
            _logger.LogWarning(ex, "Secret {Key}: DPAPI unprotect failed, treating as absent", key);
            return null;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(payload);
        }
    }

    private static byte[] Frame(byte[] magic, byte[] payload)
    {
        var framed = new byte[magic.Length + payload.Length];
        magic.CopyTo(framed, 0);
        payload.CopyTo(framed, magic.Length);
        return framed;
    }

    [SupportedOSPlatform("windows")]
    private static byte[] ProtectWindows(byte[] plaintext) =>
        ProtectedData.Protect(plaintext, Entropy, DataProtectionScope.CurrentUser);

    [SupportedOSPlatform("windows")]
    private static byte[] UnprotectWindows(byte[] ciphertext) =>
        ProtectedData.Unprotect(ciphertext, Entropy, DataProtectionScope.CurrentUser);

    // ---------- keys ----------

    private string PathFor(string key)
    {
        ValidateKey(key);
        return Path.Combine(_directory, key + FileExtension);
    }

    /// <summary>Keys are restricted to <c>[a-z0-9_.-]+</c> so they map 1:1 to safe file names.</summary>
    public static void ValidateKey(string key)
    {
        if (string.IsNullOrEmpty(key) || !KeyPattern().IsMatch(key))
        {
            throw new ArgumentException("Secret key must match [a-z0-9_.-]+", nameof(key));
        }
    }

    [GeneratedRegex("^[a-z0-9_.-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex KeyPattern();
}
