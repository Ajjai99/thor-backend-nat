using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Thor.Api.Models;

namespace Thor.Api.Services;

/// <summary>
/// Parses the public X.509 certificates that appear in SAML IdP configuration — pasted by an admin
/// (PEM or bare base64) or read from metadata (base64 DER).
/// </summary>
public static class SamlCertificateParser
{
    private const string PemHeader = "-----BEGIN CERTIFICATE-----";

    /// <summary>Throws <see cref="FormatException"/> if <paramref name="text"/> isn't a certificate.</summary>
    public static X509Certificate2 Parse(string text)
    {
        try
        {
            if (text.Contains(PemHeader, StringComparison.Ordinal))
            {
                return X509Certificate2.CreateFromPem(text);
            }

            var der = Convert.FromBase64String(string.Concat(text.Where(c => !char.IsWhiteSpace(c))));
            return X509CertificateLoader.LoadCertificate(der);
        }
        catch (CryptographicException ex)
        {
            throw new FormatException("Not a valid X.509 certificate.", ex);
        }
    }

    public static SamlCertificateResponse ToResponse(X509Certificate2 certificate) => new(
        certificate.ExportCertificatePem(),
        certificate.Subject,
        certificate.NotBefore.ToUniversalTime(),
        certificate.NotAfter.ToUniversalTime(),
        certificate.GetCertHashString(HashAlgorithmName.SHA256));
}
