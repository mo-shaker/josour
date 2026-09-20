using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Josour.Tunnel.Certificates;

namespace Josour.Tunnel.Tests;

public class SessionCertificateTests
{
    [Fact]
    public void Create_HasProtocolShape()
    {
        var now = DateTimeOffset.UtcNow;
        var expiresAt = now.AddMinutes(30);
        using var cert = SessionCertificate.Create(expiresAt, now);

        Assert.Equal("CN=josour", cert.Certificate.Subject);
        Assert.True(cert.Certificate.HasPrivateKey);
        using var ecdsa = cert.Certificate.GetECDsaPublicKey();
        Assert.NotNull(ecdsa);
        Assert.Equal(256, ecdsa!.KeySize);

        var eku = cert.Certificate.Extensions.OfType<X509EnhancedKeyUsageExtension>().Single();
        var oids = eku.EnhancedKeyUsages.Cast<Oid>().Select(o => o.Value).ToList();
        Assert.Contains(SessionCertificate.ServerAuthOid, oids);
        Assert.Contains(SessionCertificate.ClientAuthOid, oids);

        // Certificates truncate the fractional seconds
        Assert.InRange(cert.NotBefore, now.AddMinutes(-5).AddSeconds(-2), now.AddMinutes(-5).AddSeconds(1));
        Assert.InRange(cert.NotAfter, expiresAt.AddHours(1).AddSeconds(-2), expiresAt.AddHours(1).AddSeconds(1));
    }

    [Fact]
    public void Fingerprint_IsSha256OfRawData_LowercaseHex_AndStable()
    {
        using var cert = SessionCertificate.Create(DateTimeOffset.UtcNow.AddMinutes(30));
        var expected = SHA256.HashData(cert.Certificate.RawData);

        Assert.Equal(expected, cert.FingerprintSha256);
        Assert.Equal(expected, cert.FingerprintSha256);
        Assert.Equal(64, cert.FingerprintHex.Length);
        Assert.Equal(cert.FingerprintHex, cert.FingerprintHex.ToLowerInvariant());
        Assert.Equal(Convert.ToHexString(expected).ToLowerInvariant(), cert.FingerprintHex);
    }

    [Fact]
    public void Fingerprint_ReturnsDefensiveCopy()
    {
        using var cert = SessionCertificate.Create(DateTimeOffset.UtcNow.AddMinutes(30));
        var first = cert.FingerprintSha256;
        first[0] ^= 0xFF;
        Assert.NotEqual(first, cert.FingerprintSha256);
    }

    [Fact]
    public void TwoCertificates_HaveDifferentFingerprints()
    {
        using var a = SessionCertificate.Create(DateTimeOffset.UtcNow.AddMinutes(30));
        using var b = SessionCertificate.Create(DateTimeOffset.UtcNow.AddMinutes(30));
        Assert.NotEqual(a.FingerprintHex, b.FingerprintHex);
    }

    [Fact]
    public void Dispose_DisposesCertificate_ButKeepsFingerprint()
    {
        var cert = SessionCertificate.Create(DateTimeOffset.UtcNow.AddMinutes(30));
        var hex = cert.FingerprintHex;
        cert.Dispose();
        cert.Dispose(); // idempotent

        Assert.True(cert.IsDisposed);
        Assert.Equal(hex, cert.FingerprintHex);
        Assert.ThrowsAny<Exception>(() => cert.Certificate.RawData);
    }

    [Fact]
    public void Create_ForAlreadyExpiredSession_Throws()
    {
        var now = DateTimeOffset.UtcNow;
        Assert.Throws<ArgumentOutOfRangeException>(() => SessionCertificate.Create(now.AddHours(-2), now));
    }
}
