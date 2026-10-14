namespace Thor.Authorizer.Core.Auth.RefreshToken;

public interface IRefreshTokenValidator
{
    Task<RefreshTokenValidationResult> ValidateAsync(string rawRefreshToken);
}
