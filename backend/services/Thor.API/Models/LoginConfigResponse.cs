namespace Thor.Api.Models;

/// <summary>
/// The Cognito pool/app client a tenant's users sign in to, plus what the login page needs to
/// offer federated sign-in: <see cref="CognitoDomain"/> hosts <c>/oauth2/authorize</c> (null until
/// SSO is set up for the tenant), and <see cref="IdentityProviders"/> are the provider names to pass
/// as its <c>identity_provider</c>. <see cref="LogoUrl"/> is a path on the tenant's own SPA host, or
/// null if no logo is set. All public values: the SPA's app client has no secret, and the rest is
/// shown to anyone who opens the tenant's login page.
/// </summary>
public sealed record LoginConfigResponse(
    string UserPoolId,
    string AppClientId,
    string Region,
    string? CognitoDomain,
    string? LogoUrl,
    IReadOnlyList<string> IdentityProviders);
