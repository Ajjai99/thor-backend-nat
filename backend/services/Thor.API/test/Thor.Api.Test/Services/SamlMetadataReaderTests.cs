using System.Xml;
using FluentAssertions;
using Thor.Api.Services;
using Thor.Api.Test.Support;

namespace Thor.Api.Test.Services;

public class SamlMetadataReaderTests
{
    [Fact]
    public void Read_PrefersTheRedirectBindingAndSkipsEncryptionKeys()
    {
        using var signing = TestCertificates.SelfSigned("CN=Signing");
        using var encryption = TestCertificates.SelfSigned("CN=Encryption");
        var xml = $"""
            <md:EntityDescriptor xmlns:md="urn:oasis:names:tc:SAML:2.0:metadata" xmlns:ds="http://www.w3.org/2000/09/xmldsig#" entityID="https://sts.example.com/abc">
              <md:IDPSSODescriptor protocolSupportEnumeration="urn:oasis:names:tc:SAML:2.0:protocol">
                <md:KeyDescriptor use="signing"><ds:KeyInfo><ds:X509Data><ds:X509Certificate>{Convert.ToBase64String(signing.RawData)}</ds:X509Certificate></ds:X509Data></ds:KeyInfo></md:KeyDescriptor>
                <md:KeyDescriptor use="encryption"><ds:KeyInfo><ds:X509Data><ds:X509Certificate>{Convert.ToBase64String(encryption.RawData)}</ds:X509Certificate></ds:X509Data></ds:KeyInfo></md:KeyDescriptor>
                <md:SingleSignOnService Binding="urn:oasis:names:tc:SAML:2.0:bindings:HTTP-POST" Location="https://sts.example.com/post"/>
                <md:SingleSignOnService Binding="urn:oasis:names:tc:SAML:2.0:bindings:HTTP-Redirect" Location="https://sts.example.com/redirect"/>
              </md:IDPSSODescriptor>
            </md:EntityDescriptor>
            """;

        var summary = SamlMetadataReader.Read(xml);

        summary.EntityId.Should().Be("https://sts.example.com/abc");
        summary.SsoUrl.Should().Be("https://sts.example.com/redirect");
        summary.SingleLogoutUrl.Should().BeNull();
        var certificate = summary.SigningCertificates.Should().ContainSingle().Subject;
        certificate.Subject.Should().Be("CN=Signing");
        certificate.Sha256Fingerprint.Should().HaveLength(64);
        certificate.Pem.Should().StartWith("-----BEGIN CERTIFICATE-----");
    }

    [Fact]
    public void Read_RejectsDtds_SoExternalEntitiesAreNeverResolved()
    {
        const string xml = """
            <?xml version="1.0"?>
            <!DOCTYPE md:EntityDescriptor [<!ENTITY xxe SYSTEM "file:///etc/passwd">]>
            <md:EntityDescriptor xmlns:md="urn:oasis:names:tc:SAML:2.0:metadata" entityID="&xxe;"/>
            """;

        var act = () => SamlMetadataReader.Read(xml);

        act.Should().Throw<XmlException>();
    }

    [Fact]
    public void Read_MalformedXml_Throws()
    {
        var act = () => SamlMetadataReader.Read("<md:EntityDescriptor");

        act.Should().Throw<XmlException>();
    }
}
