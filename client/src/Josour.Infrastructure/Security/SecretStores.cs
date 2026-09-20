using Microsoft.Extensions.Logging;
using Josour.Core.Security;

namespace Josour.Infrastructure.Security;

/// <summary>
/// Picks the <see cref="ISecretStore"/> this machine actually has: DPAPI on Windows, an owner-only file everywhere
/// else.
/// <para>
/// One place, so that "where do the refresh token and the device secret live" has one answer per platform rather than
/// an answer per call site. Both implementations stay public and directly constructible for tests.
/// </para>
/// <para>
/// macOS used the Keychain until it could not. The Keychain binds every item to the code-signing identity of the
/// application that created it, and Josour is unsigned (ADR-0011) — so each build is a different application as far
/// as the Keychain is concerned, and every update would leave users locked out of their own secrets with
/// <c>errSecInvalidOwnerEdit</c>. That is not a defect to fix; it is what an unsigned app and the Keychain mean
/// together. <see cref="FileSecretStore"/> is what replaced it, and says plainly what that costs.
/// </para>
/// </summary>
public static class SecretStores
{
    /// <summary>The store for the running platform.</summary>
    public static ISecretStore ForCurrentPlatform(ILoggerFactory? loggerFactory = null) =>
        OperatingSystem.IsWindows()
            ? new DpapiSecretStore(loggerFactory?.CreateLogger<DpapiSecretStore>())
            : new FileSecretStore(loggerFactory?.CreateLogger<FileSecretStore>());

    /// <summary>
    /// True when the platform itself encrypts the stored bytes, which only DPAPI does. Elsewhere the file is
    /// protected by its permissions and by whatever full-disk encryption the machine has — FileVault on macOS, on
    /// by default on current hardware. Reported in diagnostics so nobody has to guess which it is.
    /// </summary>
    public static bool IsEncryptedAtRest => OperatingSystem.IsWindows();

    /// <summary>A short name for logs and the support bundle: <c>dpapi</c> or <c>file-0600</c>.</summary>
    public static string CurrentKind => OperatingSystem.IsWindows() ? "dpapi" : "file-0600";
}
