using Microsoft.Extensions.Logging;
using Thor.TenantProvisioning.Core.Abstractions;
using Thor.TenantProvisioning.Core.Models;

namespace Thor.TenantProvisioning.Core.Steps;

/// <summary>
/// Step 2 — ensures the tenant's Cognito user pool + app client exist, the pool has a domain
/// and the app client allows OAuth sign-in back to the tenant's SPA host (both needed for
/// federated SAML/OIDC sign-in), and the admin group is present. Returns the pool/client ids
/// and domain host in the state. Idempotent via the deterministic names (see
/// <see cref="ProvisioningNames"/>); re-running it for an existing tenant backfills the domain
/// and OAuth settings.
/// </summary>
public sealed class ProvisionCognitoStep(
    ICognitoProvisioner cognito,
    CognitoSignInOptions signInOptions,
    TenantRoutingOptions routingOptions,
    ILogger<ProvisionCognitoStep> logger)
{
    public async Task<ProvisioningState> RunAsync(ProvisioningState state, CancellationToken cancellationToken = default)
    {
        var poolName = ProvisioningNames.UserPool(state.Subdomain);
        var appClientName = ProvisioningNames.AppClient(state.Subdomain);
        var requestedDomainPrefix = ProvisioningNames.CognitoDomainPrefix(signInOptions.DomainPrefix, state.Subdomain);

        var pool = await cognito.EnsureUserPoolAsync(poolName, appClientName, cancellationToken);
        await cognito.EnsureAdminGroupAsync(pool.UserPoolId, ProvisioningNames.AdminGroup, cancellationToken);

        var domainPrefix = await cognito.EnsureDomainAsync(pool.UserPoolId, requestedDomainPrefix, cancellationToken);
        var domainHost = ProvisioningNames.CognitoDomainHost(domainPrefix, routingOptions.Region);

        var spaOrigin = $"https://{state.Subdomain}.{signInOptions.BaseDomain}";
        await cognito.EnsureAppClientOAuthAsync(
            pool.UserPoolId, pool.AppClientId, spaOrigin + ProvisioningNames.SignInCallbackPath, spaOrigin + "/",
            cancellationToken);

        logger.LogInformation(
            "Provisioned Cognito pool {UserPoolId} (client {AppClientId}, domain {CognitoDomain}) for subdomain {Subdomain}.",
            pool.UserPoolId, pool.AppClientId, domainHost, state.Subdomain);

        return state with { UserPoolId = pool.UserPoolId, AppClientId = pool.AppClientId, CognitoDomain = domainHost };
    }
}
