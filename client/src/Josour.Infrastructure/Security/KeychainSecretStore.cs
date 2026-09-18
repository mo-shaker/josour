using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Josour.Core.Security;

namespace Josour.Infrastructure.Security;

/// <summary>
/// <see cref="ISecretStore"/> on the macOS Keychain — the mac counterpart of <see cref="DpapiSecretStore"/>.
/// <para>
/// One generic-password item per key, under a single service name, with the key as the account. Nothing is written to
/// a file: on Windows DPAPI protects a file because that is what DPAPI does, while on macOS the Keychain *is* the
/// store, and putting an encrypted blob in <c>~/Library</c> beside it would only add a second thing to keep in step.
/// </para>
/// <para>Secret values are never logged; only key names are.</para>
/// </summary>
[SupportedOSPlatform("macos")]
public sealed partial class KeychainSecretStore : ISecretStore
{
    /// <summary>
    /// The Keychain service every Josour item lives under. Versioned, so that a future change of format can be a new
    /// service name rather than a migration that has to read the old one correctly on the first try.
    /// </summary>
    public const string DefaultService = "com.josour.client.v1";

    private readonly string _service;
    private readonly ILogger<KeychainSecretStore> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public KeychainSecretStore(ILogger<KeychainSecretStore>? logger = null)
        : this(DefaultService, logger)
    {
    }

    /// <summary>Store under an explicit service name (tests, so a run never touches the real items).</summary>
    public KeychainSecretStore(string service, ILogger<KeychainSecretStore>? logger = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(service);
        _service = service;
        _logger = logger ?? NullLogger<KeychainSecretStore>.Instance;
    }

    public string Service => _service;

    /// <summary>True on macOS: unlike the Windows store there is no development fallback here, and no need for one.</summary>
    public static bool IsProtectionAvailable => OperatingSystem.IsMacOS();

    public async Task<string?> GetAsync(string key, CancellationToken ct)
    {
        var account = Account(key);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return MacKeychain.Find(_service, account);
        }
        catch (MacKeychainException ex)
        {
            // A secret that cannot be read is the same situation as one that was never stored: the caller signs in
            // again. Failing the whole start-up over it would be worse than asking for a password.
            _logger.LogWarning(ex, "Secret {Key} could not be read from the Keychain", key);
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
        var account = Account(key);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            MacKeychain.Save(_service, account, value);
            _logger.LogDebug("Secret {Key} stored in the Keychain", key);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RemoveAsync(string key, CancellationToken ct)
    {
        var account = Account(key);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            MacKeychain.Delete(_service, account);
            _logger.LogDebug("Secret {Key} removed from the Keychain", key);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// The same key rule the Windows store applies to file names. It is not a path here, so it cannot traverse one —
    /// but two stores that accept different key sets would diverge the moment a caller used a key only one allows.
    /// </summary>
    private static string Account(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        if (!KeyPattern().IsMatch(key))
        {
            throw new ArgumentException("Key may contain only lowercase letters, digits, '.', '_' and '-'", nameof(key));
        }

        return key;
    }

    /// <summary>Character for character the pattern <see cref="DpapiSecretStore"/> uses, on purpose.</summary>
    [GeneratedRegex("^[a-z0-9_.-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex KeyPattern();
}
