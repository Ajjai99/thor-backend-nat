namespace Thor.Api.Models;

/// <summary>
/// A tenant user's role, carried as a Cognito group in the access token (see
/// <see cref="Constants.TenantConstants"/>). Roles nest — each includes everything below it — so
/// the values are ordered and a higher value satisfies any lower requirement.
/// </summary>
public enum TenantRole
{
    User = 1,
    Analyst = 2,
    Administrator = 3,
}
