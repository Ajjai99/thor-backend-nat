using Amazon.CognitoIdentityProvider;
using Amazon.CognitoIdentityProvider.Model;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Thor.Api.Models;
using Thor.DataLayer.Data;
using Thor.DataLayer.Models;

namespace Thor.Api.Services;

/// <summary>
/// Backs <c>GET /login-config/{subdomain}</c>: tells the SPA which Cognito pool/app client to
/// sign a tenant's users in to, and which federated IdPs to offer, before any token exists. The
/// subdomain is only a discovery hint here — it grants nothing; authorization comes from the token
/// the pool later issues, which the Lambda authorizer maps back to the tenant by that pool (ADR §5).
/// <para>
/// The IdP list is read from the app client and cached per pool, so anonymous traffic to this
/// route can't turn into a Cognito call per request. An unknown subdomain 404s before any Cognito call.
/// </para>
/// </summary>
public sealed class LoginConfigService(
    IMasterDbContextFactory dbContextFactory,
    MasterConnectionInfo connectionInfo,
    IAmazonCognitoIdentityProvider cognito,
    IMemoryCache cache,
    ILogger<LoginConfigService> logger)
{
    private static readonly TimeSpan IdentityProvidersTtl = TimeSpan.FromMinutes(5);

    // A failed lookup is cached too, more briefly, so a Cognito outage isn't retried on every request.
    private static readonly TimeSpan FailedLookupTtl = TimeSpan.FromSeconds(30);

    /// <summary>Returns null for an unknown subdomain or a tenant that isn't active yet.</summary>
    public async Task<LoginConfigResponse?> GetAsync(string subdomain, CancellationToken cancellationToken)
    {
        await using var db = dbContextFactory.Create(connectionInfo);

        var tenant = await db.Tenants
            .AsNoTracking()
            .Where(t => t.Subdomain == subdomain && t.StatusId == (short)TenantStatus.Active)
            .Select(t => new
            {
                t.Routing,
                LogoObjectKey = db.TenantBrandings
                    .Where(b => b.TenantId == t.TenantId)
                    .Select(b => b.LogoObjectKey)
                    .FirstOrDefault(),
            })
            .SingleOrDefaultAsync(cancellationToken);

        if (tenant?.Routing is not { } routing)
        {
            return null;
        }

        var identityProviders = await GetIdentityProvidersAsync(routing, cancellationToken);
        return new LoginConfigResponse(
            routing.UserPoolId,
            routing.AppClientId,
            routing.Region,
            routing.CognitoDomain,
            tenant.LogoObjectKey is null ? null : TenantBrandingService.LogoUrl(tenant.LogoObjectKey),
            identityProviders);
    }

    /// <summary>
    /// The federated IdPs enabled on the tenant's app client (IdentityProviderService keeps that list
    /// in sync with the pool). Local accounts (COGNITO) aren't a button, so they're left out. If
    /// Cognito can't be reached the list is empty: local sign-in still works.
    /// </summary>
    private async Task<IReadOnlyList<string>> GetIdentityProvidersAsync(TenantRouting routing, CancellationToken cancellationToken)
    {
        var cacheKey = $"login-config:idps:{routing.UserPoolId}:{routing.AppClientId}";
        if (cache.TryGetValue<IReadOnlyList<string>>(cacheKey, out var cached) && cached is not null)
        {
            return cached;
        }

        IReadOnlyList<string> providers;
        TimeSpan ttl;
        try
        {
            var described = await cognito.DescribeUserPoolClientAsync(
                new DescribeUserPoolClientRequest { UserPoolId = routing.UserPoolId, ClientId = routing.AppClientId },
                cancellationToken);

            providers = (described.UserPoolClient.SupportedIdentityProviders ?? [])
                .Where(p => !string.Equals(p, IdentityProviderService.CognitoProvider, StringComparison.Ordinal))
                .ToList();
            ttl = IdentityProvidersTtl;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Couldn't read identity providers for user pool {UserPoolId}; offering local sign-in only",
                routing.UserPoolId);
            providers = [];
            ttl = FailedLookupTtl;
        }

        cache.Set(cacheKey, providers, ttl);
        return providers;
    }
}
