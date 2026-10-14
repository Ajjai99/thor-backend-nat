namespace Thor.TenantProvisioning.Core.Models;

/// <summary>
/// Environment-level values for each tenant's hosted (OAuth) sign-in: <see cref="DomainPrefix"/>
/// (e.g. <c>thor-dev</c>) prefixes every pool's Cognito domain, and <see cref="BaseDomain"/> is the
/// parent of each tenant's SPA host (<c>{subdomain}.{BaseDomain}</c>), which the app client's
/// callback/logout URLs point at.
/// </summary>
public sealed record CognitoSignInOptions(string DomainPrefix, string BaseDomain);
