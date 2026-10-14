namespace Thor.Api.Models;

/// <summary>
/// Body of <c>PUT v{version}/identity-providers/{providerName}</c>. The SAML/OIDC settings are
/// replaced in full (a blank OIDC client secret keeps the current one); <see cref="AttributeMapping"/>,
/// <see cref="IdpIdentifiers"/> and <see cref="Vendor"/> are left unchanged when omitted, and a given
/// <see cref="AttributeMapping"/> replaces the current one (a mapping left out is removed).
/// The name and type can't change, and an update doesn't enable or disable the IdP.
/// </summary>
public sealed record UpdateTenantIdentityProviderRequest(
    string ProviderType,
    SamlProviderSettings? Saml,
    OidcProviderSettings? Oidc,
    Dictionary<string, string>? AttributeMapping,
    List<string>? IdpIdentifiers,
    string? Vendor = null);
