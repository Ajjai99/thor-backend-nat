namespace Thor.Authorizer.Core.DataAccess;

public interface IRefreshTokenRepository
{
    Task<RefreshTokenRecord?> GetByTokenIdAsync(Guid tokenId);
}
