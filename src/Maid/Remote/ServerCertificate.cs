using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Maid.Remote;

/// <summary>
/// The certificate Maid's remote endpoint presents. It is self-signed: the phone does not trust it
/// because a CA vouches for it but because pairing handed the phone its fingerprint (<see cref="PinOf"/>),
/// so nothing about it has to be public. Kept in the data folder, encrypted for the user (DPAPI).
/// </summary>
internal static class ServerCertificate
{
    private const string FileName = "remote-certificate.bin";

    public static X509Certificate2 LoadOrCreate(string dataDirectory)
    {
        var file = Path.Combine(dataDirectory, FileName);
        if (File.Exists(file))
        {
            try
            {
                var pfx = ProtectedData.Unprotect(File.ReadAllBytes(file), null, DataProtectionScope.CurrentUser);
                return X509CertificateLoader.LoadPkcs12(pfx, null);
            }
            catch (Exception e) when (e is CryptographicException or IOException)
            {
                // Made by another user, or damaged: a new one means pairing the phone again.
            }
        }

        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest($"CN=Maid on {Environment.MachineName}", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
        using var created = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(30));
        var bytes = created.Export(X509ContentType.Pfx);
        Directory.CreateDirectory(dataDirectory);
        File.WriteAllBytes(file, ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser));
        // SChannel cannot use the ephemeral key of a certificate made in memory; one loaded from PKCS#12 it can.
        return X509CertificateLoader.LoadPkcs12(bytes, null);
    }

    /// <summary>The fingerprint the phone pins: SHA-256 of the public key (SubjectPublicKeyInfo), base64url.</summary>
    public static string PinOf(X509Certificate2 certificate) =>
        Base64Url(SHA256.HashData(certificate.PublicKey.ExportSubjectPublicKeyInfo()));

    public static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
