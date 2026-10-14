using Microsoft.EntityFrameworkCore;
using Thor.Authorizer.Core.DataAccess;
using Thor.DataLayer.Data;

namespace Thor.Authorizer.Function.DataAccess;

/// <summary>
/// Looks up a connector refresh token, joined to the API key it was issued to, from the Master DB
/// as the read-only <c>thor_authorizer</c> role (see <see cref="MasterDbTenantRoutingRepository"/>
/// for the connection model). Projects only the columns needed to find the tenant, so the role's
/// column grants never include a token or key hash/salt. Returns null on no match.
/// </summary>
public sealed class MasterDbRefreshTokenRepository : IRefreshTokenRepository
{
    private readonly IMasterDbContextFactory _contextFactory;
    private readonly MasterConnectionInfo _connectionInfo;

    public MasterDbRefreshTokenRepository(IMasterDbContextFactory contextFactory, MasterConnectionInfo connectionInfo)
    {
        _contextFactory = contextFactory;
        _connectionInfo = connectionInfo;
    }

    public async Task<RefreshTokenRecord?> GetByTokenIdAsync(Guid tokenId)
    {
        await using var context = _contextFactory.Create(_connectionInfo);

        return await context.TaskApiRefreshTokens
            .AsNoTracking()
            .Where(t => t.TokenId == tokenId)
            .Select(t => new RefreshTokenRecord(t.TokenId, t.KeyId, t.ApiKey.TenantId, t.RoleType, t.RevokedAt))
            .SingleOrDefaultAsync();
    }
}
