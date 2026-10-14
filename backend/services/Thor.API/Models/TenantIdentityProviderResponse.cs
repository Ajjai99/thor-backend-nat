namespace Thor.Api.Models;

/// <summary>
/// A tenant IdP as returned by the API. Built from an allow-list of non-secret settings — the
/// OIDC client secret Cognito returns on describe is never copied here. <see cref="Enabled"/>
/// and <see cref="Vendor"/> come from Thor's record of the IdP; an IdP Thor has no record of
/// (created before it kept one) is reported disabled.
/// </summary>
public sealed record TenantIdentityProviderResponse(
    string ProviderName,
    string ProviderType,
    DateTime? CreatedAt,
    DateTime? UpdatedAt,
    SamlProviderSettingsResponse? Saml,
    OidcProviderSettingsResponse? Oidc,
    IReadOnlyDictionary<string, string> AttributeMapping,
    IReadOnlyList<string> IdpIdentifiers,
    bool Enabled,
    string? Vendor);

/// <summary>
/// SAML settings. <see cref="MetadataUrl"/> is null for a provider configured from a metadata file
/// or manual fields; <see cref="MetadataSource"/> says which (null if Thor has no record of it).
/// The certificate and endpoint fields are read from the IdP metadata Cognito holds, so an edit
/// form can be pre-filled and certificate expiry shown; they're null/empty if it can't be read.
/// <see cref="EncryptionCertificate"/> is Cognito's certificate for this IdP, which the IdP must
/// encrypt assertions with when <see cref="EncryptedResponses"/> is on.
/// </summary>
public sealed record SamlProviderSettingsResponse(
    string? MetadataUrl,
    bool? IdpSignout,
    string? EntityId,
    string? SsoUrl,
    string? SingleLogoutUrl,
    IReadOnlyList<SamlCertificateResponse> SigningCertificates,
    string? MetadataSource,
    bool EncryptedResponses,
    bool SignRequests,
    SamlCertificateResponse? EncryptionCertificate);

/// <summary>
/// One of the IdP's public signing certificates. <see cref="Sha256Fingerprint"/> is the hex SHA-256
/// of the DER certificate — what most IdP consoles display.
/// </summary>
public sealed record SamlCertificateResponse(
    string Pem,
    string Subject,
    DateTimeOffset NotBefore,
    DateTimeOffset NotAfter,
    string Sha256Fingerprint);

public sealed record OidcProviderSettingsResponse(
    string? ClientId, string? Issuer, string? AuthorizeScopes, string? AttributesRequestMethod);
