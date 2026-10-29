using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging;
using Sofabird.Core.Protocol;

namespace Sofabird.Companion.Net;

/// <summary>
/// The companion's certificate for the encrypted WebSocket (<c>wss</c>): self-signed (ECDSA P-256, 20 years), kept in
/// the current user's certificate store ("Personal", so Windows protects the private key like the user's other keys)
/// and found by its thumbprint (<c>tlsThumbprint</c> in the settings). A phone trusts it because it pinned its SHA-256
/// when it paired (<see cref="TlsInfo.Sha256"/>), not through any authority, so it never needs renewing for a name.
/// </summary>
public static class TlsCertificate
{
    private const string ServerAuth = "1.3.6.1.5.5.7.3.1";
    private static readonly TimeSpan RenewBefore = TimeSpan.FromDays(30);

    /// <summary>The certificate, created at the first start; <c>null</c> when it can be neither found nor made (no wss then).</summary>
    public static X509Certificate2? LoadOrCreate(Settings settings, SettingsStore store, ILogger log)
    {
        try
        {
            using var my = new X509Store(StoreName.My, StoreLocation.CurrentUser);
            my.Open(OpenFlags.ReadWrite);
            if (settings.TlsThumbprint is { Length: > 0 } thumbprint)
            {
                foreach (var found in my.Certificates.Find(X509FindType.FindByThumbprint, thumbprint, validOnly: false))
                {
                    if (found.HasPrivateKey && found.NotAfter - DateTime.Now > RenewBefore) return found;
                    found.Dispose();
                }
                log.LogWarning("TLS: the certificate {Thumbprint} is gone or expires; making a new one (phones pair again)", thumbprint);
            }
            var cert = Create();
            my.Add(cert);
            store.Update(s => s.TlsThumbprint = cert.Thumbprint);
            log.LogInformation("TLS: made the certificate {Thumbprint} (SHA-256 {Sha256})", cert.Thumbprint, Fingerprint(cert));
            return cert;
        }
        catch (Exception ex) when (ex is CryptographicException or PlatformNotSupportedException or UnauthorizedAccessException)
        {
            log.LogWarning(ex, "TLS: no certificate; phones connect without encryption");
            return null;
        }
    }

    /// <summary>What phones pin: SHA-256 of the certificate (DER), upper-case hex.</summary>
    public static string Fingerprint(X509Certificate2 cert) => Convert.ToHexString(SHA256.HashData(cert.RawData));

    private static X509Certificate2 Create()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=Sofabird " + Environment.MachineName, key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid(ServerAuth)], false));
        using var made = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(20));
        // Re-imported with a persisted key: Windows' TLS (SChannel) cannot use the ephemeral key of a new certificate.
        var cert = X509CertificateLoader.LoadPkcs12(made.Export(X509ContentType.Pkcs12), null,
            X509KeyStorageFlags.UserKeySet | X509KeyStorageFlags.PersistKeySet);
        cert.FriendlyName = "Sofabird (encrypted connection to phones)";
        return cert;
    }
}
