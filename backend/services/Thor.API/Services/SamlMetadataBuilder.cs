using System.Xml.Linq;
using Thor.Api.Exceptions;
using Thor.Api.Models;

namespace Thor.Api.Services;

/// <summary>
/// Builds the SAML 2.0 IdP metadata document Cognito needs (it accepts only metadata) from an IdP
/// entered field by field. Every value is validated first, and the XML is built with
/// <see cref="XDocument"/> so admin-entered values are always escaped.
/// </summary>
public static class SamlMetadataBuilder
{
    private static readonly XNamespace Md = "urn:oasis:names:tc:SAML:2.0:metadata";
    private static readonly XNamespace Ds = "http://www.w3.org/2000/09/xmldsig#";

    private const string RedirectBinding = "urn:oasis:names:tc:SAML:2.0:bindings:HTTP-Redirect";
    private const string PostBinding = "urn:oasis:names:tc:SAML:2.0:bindings:HTTP-POST";

    public static string Build(SamlManualSettings manual, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(manual.EntityId))
        {
            throw new InvalidIdentityProviderException("saml.manual.entityId is required.");
        }

        var ssoUrl = RequireHttpsUrl(manual.SsoUrl, "saml.manual.ssoUrl");
        var sloUrl = string.IsNullOrWhiteSpace(manual.SingleLogoutUrl)
            ? null
            : RequireHttpsUrl(manual.SingleLogoutUrl, "saml.manual.singleLogoutUrl");

        if (string.IsNullOrWhiteSpace(manual.SigningCertificate))
        {
            throw new InvalidIdentityProviderException("saml.manual.signingCertificate is required.");
        }

        string certificateBase64;
        try
        {
            using var certificate = SamlCertificateParser.Parse(manual.SigningCertificate);
            if (certificate.NotAfter.ToUniversalTime() <= now)
            {
                throw new InvalidIdentityProviderException(
                    $"saml.manual.signingCertificate expired on {certificate.NotAfter.ToUniversalTime():yyyy-MM-dd}.");
            }

            certificateBase64 = Convert.ToBase64String(certificate.RawData);
        }
        catch (FormatException ex)
        {
            throw new InvalidIdentityProviderException(
                "saml.manual.signingCertificate isn't a valid X.509 certificate (PEM or base64).", ex);
        }

        // Element order follows the metadata schema: KeyDescriptor, SingleLogoutService, SingleSignOnService.
        var descriptor = new XElement(Md + "IDPSSODescriptor",
            new XAttribute("protocolSupportEnumeration", "urn:oasis:names:tc:SAML:2.0:protocol"),
            new XElement(Md + "KeyDescriptor",
                new XAttribute("use", "signing"),
                new XElement(Ds + "KeyInfo",
                    new XElement(Ds + "X509Data",
                        new XElement(Ds + "X509Certificate", certificateBase64)))));

        if (sloUrl is not null)
        {
            descriptor.Add(Endpoint("SingleLogoutService", RedirectBinding, sloUrl));
            descriptor.Add(Endpoint("SingleLogoutService", PostBinding, sloUrl));
        }

        descriptor.Add(Endpoint("SingleSignOnService", RedirectBinding, ssoUrl));
        descriptor.Add(Endpoint("SingleSignOnService", PostBinding, ssoUrl));

        var entity = new XElement(Md + "EntityDescriptor",
            new XAttribute(XNamespace.Xmlns + "md", Md),
            new XAttribute(XNamespace.Xmlns + "ds", Ds),
            new XAttribute("entityID", manual.EntityId.Trim()),
            descriptor);

        return entity.ToString(SaveOptions.DisableFormatting);
    }

    private static XElement Endpoint(string name, string binding, string location) =>
        new(Md + name, new XAttribute("Binding", binding), new XAttribute("Location", location));

    private static string RequireHttpsUrl(string? value, string field)
    {
        var trimmed = value?.Trim();
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidIdentityProviderException($"{field} must be an absolute https URL.");
        }

        return trimmed!;
    }
}
