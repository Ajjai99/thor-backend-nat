using System.Xml;
using System.Xml.Linq;
using Thor.Api.Models;

namespace Thor.Api.Services;

/// <summary>
/// Reads the fields an edit form needs (entity ID, SSO/SLO URLs, signing certificates) from SAML IdP
/// metadata. The metadata was supplied by a tenant admin, so DTDs and external resolution are
/// disabled (no XXE / entity expansion).
/// </summary>
public static class SamlMetadataReader
{
    private static readonly XNamespace Md = "urn:oasis:names:tc:SAML:2.0:metadata";
    private static readonly XNamespace Ds = "http://www.w3.org/2000/09/xmldsig#";

    private const string RedirectBinding = "urn:oasis:names:tc:SAML:2.0:bindings:HTTP-Redirect";

    private static readonly XmlReaderSettings SafeSettings = new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
    };

    public sealed record SamlMetadataSummary(
        string? EntityId,
        string? SsoUrl,
        string? SingleLogoutUrl,
        IReadOnlyList<SamlCertificateResponse> SigningCertificates);

    /// <summary>
    /// Throws <see cref="XmlException"/> for malformed or DTD-bearing XML, and
    /// <see cref="FormatException"/> for an unreadable certificate.
    /// </summary>
    public static SamlMetadataSummary Read(string metadataXml)
    {
        using var reader = XmlReader.Create(new StringReader(metadataXml), SafeSettings);
        var document = XDocument.Load(reader);

        var entity = document.Root?.Name == Md + "EntityDescriptor"
            ? document.Root
            : document.Descendants(Md + "EntityDescriptor").FirstOrDefault();
        var descriptor = entity?.Element(Md + "IDPSSODescriptor");

        var certificates = (descriptor?.Elements(Md + "KeyDescriptor") ?? [])
            .Where(k => (string?)k.Attribute("use") is null or "signing")
            .SelectMany(k => k.Descendants(Ds + "X509Certificate"))
            .Select(c =>
            {
                using var certificate = SamlCertificateParser.Parse(c.Value);
                return SamlCertificateParser.ToResponse(certificate);
            })
            .ToList();

        return new SamlMetadataSummary(
            (string?)entity?.Attribute("entityID"),
            PreferredLocation(descriptor, "SingleSignOnService"),
            PreferredLocation(descriptor, "SingleLogoutService"),
            certificates);
    }

    // Cognito sends requests with the HTTP-Redirect binding, so that endpoint is the one that matters.
    private static string? PreferredLocation(XElement? descriptor, string name)
    {
        var endpoints = descriptor?.Elements(Md + name).ToList() ?? [];
        var endpoint = endpoints.FirstOrDefault(e => (string?)e.Attribute("Binding") == RedirectBinding)
            ?? endpoints.FirstOrDefault();
        return (string?)endpoint?.Attribute("Location");
    }
}
