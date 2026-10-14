namespace Thor.Api.Models;

/// <summary>
/// Response of <c>GET v{version}/sso/service-provider</c>: what a tenant admin enters in their IdP
/// when registering Thor (the tenant's Cognito user pool) as a SAML service provider or OIDC client.
/// <see cref="SigningCertificate"/> is Cognito's public certificate, for IdPs that verify signed
/// requests or sign-out messages.
/// </summary>
public sealed record ServiceProviderInfoResponse(
    string SamlEntityId,
    string SamlAcsUrl,
    string SamlSignOutUrl,
    string OidcRedirectUri,
    SamlCertificateResponse SigningCertificate);
