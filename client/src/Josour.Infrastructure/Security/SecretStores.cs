using Microsoft.Extensions.Logging;
using Josour.Core.Security;

namespace Josour.Infrastructure.Security;

/// <summary>
/// Picks the <see cref="ISecretStore"/> this machine actually has: DPAPI on Windows, the Keychain on macOS.
/// <para>
/// One place, so that "where do the refresh token and the device secret live" has one answer per platform rather than
/// an answer per call site. Both implementations stay public and directly constructible for tests.
/// </para>
/// </summary>
public static class SecretStores
{
    /// <summary>
    /// The store for the running platform.
    /// <para>
    /// On anything that is neither Windows nor macOS this is <see cref="DpapiSecretStore"/> in its clearly marked
    /// PLAINTEXT development mode, which is what CI on Linux and a developer's stray build get. It is not a shipping
    /// configuration and <see cref="IsProtected"/> says so.
    /// </para>
    /// </summary>
    public static ISecretStore ForCurrentPlatform(ILoggerFactory? loggerFactory = null) =>
        OperatingSystem.IsMacOS()
            ? new KeychainSecretStore(loggerFactory?.CreateLogger<KeychainSecretStore>())
            : new DpapiSecretStore(loggerFactory?.CreateLogger<DpapiSecretStore>());

    /// <summary>
    /// True when the platform really protects what <see cref="ForCurrentPlatform"/> stores. False means the plaintext
    /// development fallback — worth refusing to ship, and worth saying out loud in diagnostics.
    /// </summary>
    public static bool IsProtected => OperatingSystem.IsWindows() || OperatingSystem.IsMacOS();

    /// <summary>A short name for logs and the support bundle: <c>dpapi</c>, <c>keychain</c> or <c>plaintext</c>.</summary>
    public static string CurrentKind => OperatingSystem.IsMacOS()
        ? "keychain"
        : OperatingSystem.IsWindows() ? "dpapi" : "plaintext";
}
