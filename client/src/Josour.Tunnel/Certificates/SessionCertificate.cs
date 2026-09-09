using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Josour.Tunnel.Certificates;

/// <summary>
/// شهادة ذاتية مؤقتة لجلسة واحدة (docs/protocol.md القسم 2 الخطوة 1):
/// ECDSA P-256، CN=josour، EKU serverAuth+clientAuth، الصلاحية من الآن − 5 دقائق إلى expires_at + ساعة.
/// Dispose يحذف المفتاح الخاص (على Windows: حاوية المفتاح لأن الاستيراد بـ UserKeySet بلا PersistKeySet).
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

    /// <summary>SHA-256 على RawData (32 بايت). نسخة جديدة في كل استدعاء.</summary>
    public byte[] FingerprintSha256 => (byte[])_fingerprint.Clone();

    /// <summary>البصمة hex بأحرف صغيرة (64 حرفًا) كما تُرسل في session.endpoint.</summary>
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

    // WINDOWS-ONLY: CreateSelfSigned يعطي مفتاح CNG مؤقتًا لا يستطيع Schannel استخدامه. نصدّر PFX ونعيد الاستيراد
    // بـ UserKeySet (بلا PersistKeySet) فيُحفظ المفتاح في حاوية مستخدم تُحذف عند Dispose للشهادة.
    // لم يُتحقق منه إلا بمراجعة الكود على هذا الجهاز (macOS)؛ يحتاج تشغيلًا فعليًا على Windows 10 و11.
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
