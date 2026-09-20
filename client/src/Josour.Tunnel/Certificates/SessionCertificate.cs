using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Josour.Tunnel.Certificates;

/// <summary>
/// A temporary self-signed certificate for one session (docs/protocol.md section 2 step 1):
/// ECDSA P-256, CN=josour, EKU serverAuth+clientAuth, valid from now − 5 minutes to expires_at + an hour.
/// Dispose deletes the private key (on Windows: the key container, because the import uses UserKeySet without PersistKeySet).
/// </summary>
public sealed class SessionCertificate : IDisposable
{
    public const string SubjectName = "CN=josour";
    public const string ServerAuthOid = "1.3.6.1.5.5.7.3.1";
    public const string ClientAuthOid = "1.3.6.1.5.5.7.3.2";

    private readonly byte[] _fingerprint;
    private bool _disposed;

    private SessionCertificate(X509Certificate2 certificate)
    {
        Certificate = certificate;
        _fingerprint = SHA256.HashData(certificate.RawData);
        FingerprintHex = Convert.ToHexString(_fingerprint).ToLowerInvariant();
        NotBefore = new DateTimeOffset(certificate.NotBefore.ToUniversalTime());
        NotAfter = new DateTimeOffset(certificate.NotAfter.ToUniversalTime());
    }

    public X509Certificate2 Certificate { get; }

    /// <summary>SHA-256 over RawData (32 bytes). A fresh copy on every call.</summary>
    public byte[] FingerprintSha256 => (byte[])_fingerprint.Clone();

    /// <summary>The fingerprint as lowercase hex (64 characters) as it is sent in session.endpoint.</summary>
    public string FingerprintHex { get; }

    public DateTimeOffset NotBefore { get; }
    public DateTimeOffset NotAfter { get; }
    public bool IsDisposed => _disposed;

    public static SessionCertificate Create(DateTimeOffset expiresAt) => Create(expiresAt, DateTimeOffset.UtcNow);

    public static SessionCertificate Create(DateTimeOffset expiresAt, DateTimeOffset now)
    {
        var notBefore = now.AddMinutes(-5);
        var notAfter = expiresAt.AddHours(1);
        if (notAfter <= notBefore)
            throw new ArgumentOutOfRangeException(nameof(expiresAt), "expiresAt + 1h must be later than now - 5min");

        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest(SubjectName, key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, critical: true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, critical: true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new Oid(ServerAuthOid), new Oid(ClientAuthOid) }, critical: false));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, critical: false));

        var created = request.CreateSelfSigned(notBefore, notAfter);
        return new SessionCertificate(OperatingSystem.IsWindows() ? ReimportForSchannel(created) : created);
    }

    // WINDOWS-ONLY: CreateSelfSigned gives an ephemeral CNG key that Schannel cannot use. We export a PFX and re-import it
    // with UserKeySet (without PersistKeySet), so the key is stored in a user container that is deleted when the certificate is disposed.
    // Only verified by reading the code on this machine (macOS); it needs an actual run on Windows 10 and 11.
    private static X509Certificate2 ReimportForSchannel(X509Certificate2 ephemeral)
    {
        var pfx = ephemeral.Export(X509ContentType.Pfx);
        try
        {
            return new X509Certificate2(pfx, (string?)null, X509KeyStorageFlags.UserKeySet);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pfx);
            ephemeral.Dispose();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Certificate.Dispose();
    }
}
