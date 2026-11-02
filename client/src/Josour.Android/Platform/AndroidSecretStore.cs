using System.Security.Cryptography;
using System.Text;
using Java.Security;
using Javax.Crypto;
using Javax.Crypto.Spec;
using Josour.Core.Security;
using Josour.Infrastructure;
using Microsoft.Extensions.Logging;
using Android.Security.Keystore;

namespace Josour.Droid.Platform;

/// <summary>
/// The device secret and the refresh token, encrypted with an AES-256-GCM key that lives in the Android Keystore (hardware
/// backed where the phone has it) and never leaves it. The ciphertext sits in the app's private storage, so a backup or a
/// copy of the files without the key is useless. Android keeps the key per signing identity, which a phone's app has a
/// stable one of — the reason this works here where the Keychain did not on an unsigned Mac build.
/// </summary>
internal sealed class AndroidSecretStore : ISecretStore
{
    private const string KeyAlias = "josour-secrets";
    private const string Provider = "AndroidKeyStore";
    private const int TagBits = 128;
    private readonly string _directory = Path.Combine(AppPaths.SecretsDirectory, "keystore");
    private readonly ILogger<AndroidSecretStore> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public AndroidSecretStore(ILoggerFactory loggers) => _logger = loggers.CreateLogger<AndroidSecretStore>();

    public async Task<string?> GetAsync(string key, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var path = PathFor(key);
            if (!File.Exists(path)) return null;
            var blob = await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
            if (blob.Length <= 12) return null;
            var cipher = Cipher.GetInstance("AES/GCM/NoPadding")!;
            cipher.Init(Javax.Crypto.CipherMode.DecryptMode, GetOrCreateKey(), new GCMParameterSpec(TagBits, blob, 0, 12));
            var plain = cipher.DoFinal(blob, 12, blob.Length - 12)!;
            return Encoding.UTF8.GetString(plain);
        }
        catch (Exception ex) when (ex is Java.Lang.Exception or IOException)
        {
            // A key that was wiped (a factory reset of the keystore, a restore onto another phone) cannot decrypt: the
            // secret is as good as absent, and the next sign-in stores a new one.
            _logger.LogWarning("Could not read the stored secret {Key}: {Message}", key, ex.Message);
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SetAsync(string key, string value, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var cipher = Cipher.GetInstance("AES/GCM/NoPadding")!;
            cipher.Init(Javax.Crypto.CipherMode.EncryptMode, GetOrCreateKey());
            var iv = cipher.GetIV()!;
            var sealedBytes = cipher.DoFinal(Encoding.UTF8.GetBytes(value))!;
            var blob = new byte[iv.Length + sealedBytes.Length];
            iv.CopyTo(blob, 0);
            sealedBytes.CopyTo(blob, iv.Length);

            Directory.CreateDirectory(_directory);
            var path = PathFor(key);
            var temp = path + ".tmp";
            await File.WriteAllBytesAsync(temp, blob, ct).ConfigureAwait(false);
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RemoveAsync(string key, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var path = PathFor(key);
            if (File.Exists(path)) File.Delete(path);
        }
        finally
        {
            _gate.Release();
        }
    }

    private string PathFor(string key) =>
        Path.Combine(_directory, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..32].ToLowerInvariant() + ".bin");

    private static IKey GetOrCreateKey()
    {
        var store = KeyStore.GetInstance(Provider)!;
        store.Load(null);
        if (store.GetKey(KeyAlias, null) is IKey existing) return existing;

        var generator = KeyGenerator.GetInstance(KeyProperties.KeyAlgorithmAes, Provider)!;
        generator.Init(new KeyGenParameterSpec.Builder(KeyAlias, KeyStorePurpose.Encrypt | KeyStorePurpose.Decrypt)
            .SetBlockModes(KeyProperties.BlockModeGcm)
            .SetEncryptionPaddings(KeyProperties.EncryptionPaddingNone)
            .SetKeySize(256)
            .Build());
        return generator.GenerateKey()!;
    }
}
