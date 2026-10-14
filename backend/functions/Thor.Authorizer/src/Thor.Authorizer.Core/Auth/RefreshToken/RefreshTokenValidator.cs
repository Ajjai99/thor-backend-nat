using Thor.Auth;
using Thor.Authorizer.Core.DataAccess;

namespace Thor.Authorizer.Core.Auth.RefreshToken;

/// <summary>
/// Resolves a connector refresh token (<c>thor_rt_{token_id}_{secret}</c>) to its tenant: the
/// token must exist in the Master DB and not have been spent (revoked). Its secret is NOT verified
/// here — Thor.Api's ConnectorAuthService fully verifies (hash, expiry, key status) and rotates
/// the token when the request reaches it. The authorizer's job is only to find the tenant, which
/// comes from the token's API key record, never from the request (ADR §5).
/// </summary>
public sealed class RefreshTokenValidator : IRefreshTokenValidator
{
    private readonly IRefreshTokenRepository _repository;

    public RefreshTokenValidator(IRefreshTokenRepository repository) => _repository = repository;

    public async Task<RefreshTokenValidationResult> ValidateAsync(string rawRefreshToken)
    {
        if (!PrefixedSecretToken.TryParse(rawRefreshToken, ThorTokenIssuer.RefreshTokenPrefix, out var tokenId, out _))
        {
            return RefreshTokenValidationResult.Failure("malformed refresh token");
        }

        var record = await _repository.GetByTokenIdAsync(tokenId);
        if (record is null)
        {
            return RefreshTokenValidationResult.Failure("refresh token not found");
        }

        if (record.RevokedAt is not null)
        {
            return RefreshTokenValidationResult.Failure("refresh token already used");
        }

        return RefreshTokenValidationResult.Success(record.TenantId.ToString(), record.KeyId.ToString(), record.RoleType);
    }
}
