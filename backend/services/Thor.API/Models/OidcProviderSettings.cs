namespace Thor.Api.Models;

/// <summary>
/// OIDC IdP settings. <see cref="ClientSecret"/> is passed straight to Cognito — Thor never
/// persists, logs, or returns it. It's required on create; on update, leaving it blank keeps the
/// secret Cognito already holds.
/// </summary>
public sealed record OidcProviderSettings(
    string ClientId,
    string? ClientSecret,
    string Issuer,
    string AuthorizeScopes,
    string? AttributesRequestMethod);
