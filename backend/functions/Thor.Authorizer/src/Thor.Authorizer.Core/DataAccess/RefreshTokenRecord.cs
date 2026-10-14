namespace Thor.Authorizer.Core.DataAccess;

/// <summary>
/// A stored connector refresh token and the tenant of the API key it was issued to — only what
/// the authorizer needs to find the tenant; the token's hash and salt are never read here.
/// </summary>
public sealed record RefreshTokenRecord(
    Guid TokenId,
    Guid KeyId,
    Guid TenantId,
    string RoleType,
    DateTimeOffset? RevokedAt
);
