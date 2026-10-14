using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Thor.Api.Test.Support;

/// <summary>
/// Self-signed certificates generated per test run, so no certificate or key material is committed.
/// Only the public certificate is ever handed to code under test.
/// </summary>
public static class TestCertificates
{
    public static X509Certificate2 SelfSigned(
        string subject = "CN=idp.example.com", DateTimeOffset? notBefore = null, DateTimeOffset? notAfter = null)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest(subject, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return request.CreateSelfSigned(
            notBefore ?? DateTimeOffset.UtcNow.AddDays(-1),
            notAfter ?? DateTimeOffset.UtcNow.AddYears(1));
    }

    public static string ValidPem() => SelfSigned().ExportCertificatePem();

    public static string ExpiredPem() =>
        SelfSigned(notBefore: DateTimeOffset.UtcNow.AddYears(-2), notAfter: DateTimeOffset.UtcNow.AddDays(-1))
            .ExportCertificatePem();
}
