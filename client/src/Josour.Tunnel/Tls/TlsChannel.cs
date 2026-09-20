using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Josour.Tunnel.Tls;

/// <summary>The negotiated version is below TLS 1.2 (ADR-0002). The connection was closed before throwing.</summary>
public sealed class TlsTooOldException : AuthenticationException
{
    public TlsTooOldException(SslProtocols negotiated)
        : base($"negotiated TLS protocol {negotiated} is older than TLS 1.2") => Negotiated = negotiated;

    public SslProtocols Negotiated { get; }
}

/// <summary>The handshake's result: the SslStream (which owns the inner Stream) and the version in wire form ("1.2"/"1.3").</summary>
public sealed record TlsHandshakeResult(SslStream Stream, string TlsVersion, SslProtocols Protocol);

/// <summary>
/// The TLS handshake per docs/protocol.md section 3: SslProtocols.None, a fixed TargetHost, pinning the certificate's SHA-256 fingerprint,
/// ignoring chain and name errors, with no revocation check, no client certificate, and refusing any version below TLS 1.2 after the handshake.
/// On any failure the SslStream (and with it the inner Stream) is closed and then the exception is thrown.
/// </summary>
public static class TlsChannel
{
    public const string TargetHost = "josour";
    public const int FingerprintLength = 32;

    public static async Task<TlsHandshakeResult> AuthenticateAsServerAsync(Stream inner, X509Certificate2 certificate, TimeSpan timeout, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(certificate);
        var ssl = new SslStream(inner, leaveInnerStreamOpen: false);
        try
        {
            var options = new SslServerAuthenticationOptions
            {
                ServerCertificate = certificate,
                ClientCertificateRequired = false,
                EnabledSslProtocols = SslProtocols.None,
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                EncryptionPolicy = EncryptionPolicy.RequireEncryption,
                AllowRenegotiation = false,
            };
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);
            try
            {
                await ssl.AuthenticateAsServerAsync(options, cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new TimeoutException("TLS server handshake timed out");
            }
            return Finish(ssl);
        }
        catch
        {
            await ssl.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public static async Task<TlsHandshakeResult> AuthenticateAsClientAsync(Stream inner, byte[] expectedFingerprint, TimeSpan timeout, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(expectedFingerprint);
        if (expectedFingerprint.Length != FingerprintLength)
            throw new ArgumentException($"fingerprint must be {FingerprintLength} bytes", nameof(expectedFingerprint));
        var pinned = (byte[])expectedFingerprint.Clone();

        var ssl = new SslStream(inner, leaveInnerStreamOpen: false);
        try
        {
            var options = new SslClientAuthenticationOptions
            {
                TargetHost = TargetHost,
                EnabledSslProtocols = SslProtocols.None,
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                EncryptionPolicy = EncryptionPolicy.RequireEncryption,
                AllowRenegotiation = false,
                ClientCertificates = null,
                // Chain and name errors are deliberately ignored: acceptance rests on the pinned fingerprint alone.
                RemoteCertificateValidationCallback = (_, certificate, _, _) =>
                    certificate is not null && CryptographicOperations.FixedTimeEquals(ComputeFingerprint(certificate), pinned),
            };
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);
            try
            {
                await ssl.AuthenticateAsClientAsync(options, cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new TimeoutException("TLS client handshake timed out");
            }
            return Finish(ssl);
        }
        catch
        {
            await ssl.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>SHA-256 over the certificate in DER form (RawData).</summary>
    public static byte[] ComputeFingerprint(X509Certificate certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        return SHA256.HashData(certificate.GetRawCertData());
    }

    public static bool IsAtLeastTls12(SslProtocols protocol)
        => protocol == SslProtocols.Tls12 || protocol == SslProtocols.Tls13 || (int)protocol > (int)SslProtocols.Tls13;

    /// <summary>The version's wire form as in session.connected.tls_version.</summary>
    public static string ToWireVersion(SslProtocols protocol) => protocol switch
    {
        SslProtocols.Tls12 => "1.2",
        SslProtocols.Tls13 => "1.3",
        _ => protocol.ToString(),
    };

    private static TlsHandshakeResult Finish(SslStream ssl)
    {
        var protocol = ssl.SslProtocol;
        if (!IsAtLeastTls12(protocol)) throw new TlsTooOldException(protocol);
        return new TlsHandshakeResult(ssl, ToWireVersion(protocol), protocol);
    }
}
