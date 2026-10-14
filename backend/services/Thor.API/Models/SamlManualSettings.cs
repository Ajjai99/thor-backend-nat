namespace Thor.Api.Models;

/// <summary>
/// A SAML IdP entered field by field. Thor builds the IdP metadata document Cognito needs from
/// these. <see cref="SigningCertificate"/> is the IdP's public X.509 signing certificate, as PEM
/// or bare base64; <see cref="SingleLogoutUrl"/> is only needed for IdP sign-out.
/// </summary>
public sealed record SamlManualSettings(
    string EntityId,
    string SsoUrl,
    string SigningCertificate,
    string? SingleLogoutUrl);
