namespace Thor.Authorizer.Core.Auth.RefreshToken;

public sealed record RefreshTokenValidationResult(
    bool IsValid,
    string TenantId,
    string KeyId,
    string RoleType,
    string? FailureReason)
{
    public static RefreshTokenValidationResult Success(string tenantId, string keyId, string roleType) =>
        new(true, tenantId, keyId, roleType, null);

    public static RefreshTokenValidationResult Failure(string reason) =>
        new(false, string.Empty, string.Empty, string.Empty, reason);
}
