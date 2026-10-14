using System.Xml.Linq;
using FluentAssertions;
using Thor.Api.Exceptions;
using Thor.Api.Models;
using Thor.Api.Services;
using Thor.Api.Test.Support;

namespace Thor.Api.Test.Services;

public class SamlMetadataBuilderTests
{
    private static readonly XNamespace Md = "urn:oasis:names:tc:SAML:2.0:metadata";

    private static SamlManualSettings Manual(string? certificate = null) => new(
        "http://www.okta.com/exk123",
        "https://acme.okta.com/app/thor/exk123/sso/saml",
        certificate ?? TestCertificates.ValidPem(),
        null);

    [Fact]
    public void Build_ProducesMetadataTheReaderRoundTrips()
    {
        using var certificate = TestCertificates.SelfSigned("CN=Acme IdP");
        var manual = Manual(certificate.ExportCertificatePem()) with
        {
            SingleLogoutUrl = "https://acme.okta.com/app/thor/exk123/slo/saml",
        };

        var xml = SamlMetadataBuilder.Build(manual, DateTimeOffset.UtcNow);
        var summary = SamlMetadataReader.Read(xml);

        summary.EntityId.Should().Be(manual.EntityId);
        summary.SsoUrl.Should().Be(manual.SsoUrl);
        summary.SingleLogoutUrl.Should().Be(manual.SingleLogoutUrl);
        summary.SigningCertificates.Should().ContainSingle()
            .Which.Subject.Should().Be("CN=Acme IdP");
    }

    [Fact]
    public void Build_AcceptsBareBase64Certificates()
    {
        using var certificate = TestCertificates.SelfSigned();

        var xml = SamlMetadataBuilder.Build(Manual(Convert.ToBase64String(certificate.RawData)), DateTimeOffset.UtcNow);

        xml.Should().Contain(Convert.ToBase64String(certificate.RawData));
    }

    [Fact]
    public void Build_EscapesAdminEnteredValues()
    {
        var xml = SamlMetadataBuilder.Build(
            Manual() with { EntityId = "urn:acme\"/><evil attr=\"&" }, DateTimeOffset.UtcNow);

        XDocument.Parse(xml).Root!.Attribute("entityID")!.Value.Should().Be("urn:acme\"/><evil attr=\"&");
        XDocument.Parse(xml).Descendants(Md + "evil").Should().BeEmpty();
    }

    [Fact]
    public void Build_WithoutSingleLogoutUrl_OmitsLogoutService()
    {
        var xml = SamlMetadataBuilder.Build(Manual(), DateTimeOffset.UtcNow);

        XDocument.Parse(xml).Descendants(Md + "SingleLogoutService").Should().BeEmpty();
    }

    public static TheoryData<SamlManualSettings> InvalidSettings() => new()
    {
        Manual() with { EntityId = " " },
        Manual() with { SsoUrl = "http://acme.okta.com/sso" },
        Manual() with { SsoUrl = "/relative/sso" },
        Manual() with { SingleLogoutUrl = "ftp://acme.okta.com/slo" },
        Manual(""),
        Manual("not a certificate"),
        Manual("-----BEGIN CERTIFICATE-----\nAAAA\n-----END CERTIFICATE-----"),
        Manual(TestCertificates.ExpiredPem()),
    };

    [Theory]
    [MemberData(nameof(InvalidSettings))]
    public void Build_InvalidSettings_Throws(SamlManualSettings manual)
    {
        var act = () => SamlMetadataBuilder.Build(manual, DateTimeOffset.UtcNow);

        act.Should().Throw<InvalidIdentityProviderException>();
    }
}
