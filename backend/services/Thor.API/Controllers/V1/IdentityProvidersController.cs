using Asp.Versioning;
using Microsoft.AspNetCore.Mvc;
using Thor.Api.Constants;
using Thor.Api.Exceptions;
using Thor.Api.Filters;
using Thor.Api.Middleware;
using Thor.Api.Models;
using Thor.Api.Services;
using Thor.DataConnectionManager.Exceptions;

namespace Thor.Api.Controllers.V1;

/// <summary>
/// Tenant-admin management of the tenant's SAML/OIDC identity providers (see
/// <see cref="IdentityProviderService"/>). Every action requires the caller to be a tenant admin.
/// IdPs are created disabled; <c>enable</c> / <c>disable</c> put them on and off the login page.
/// </summary>
[ApiController]
[ApiVersion("1.0")]
[Route("v{version:apiVersion}/identity-providers")]
[RequireTenantRole(TenantRole.Administrator)]
public class IdentityProvidersController(
    IdentityProviderService identityProviderService,
    ILogger<IdentityProvidersController> logger) : ControllerBase
{
    // Create/update carry SAML metadata inline, so the WAF exempts them from its 8KB body limit
    // (infra body-size-limit rules); this is their cap instead. Real-world metadata is well under it.
    private const long MaxProviderBodyBytes = 256 * 1024;

    // X-THOR-TENANT-ID is set by API Gateway from the Lambda authorizer's verified context, and
    // CognitoAuthMiddleware has already verified the token against that tenant, so it's read directly.
    // The admin check uses the token's groups (RequireTenantRoleAttribute), not a header.
    [HttpGet]
    [ProducesResponseType<IReadOnlyList<TenantIdentityProviderSummaryResponse>>(StatusCodes.Status200OK)]
    public Task<IActionResult> List(CancellationToken cancellationToken) =>
        HandleAsync(async tenantId => Ok(await identityProviderService.ListAsync(tenantId, cancellationToken)));

    [HttpGet("{providerName}")]
    [ProducesResponseType<TenantIdentityProviderResponse>(StatusCodes.Status200OK)]
    public Task<IActionResult> Get(string providerName, CancellationToken cancellationToken) =>
        HandleAsync(async tenantId => Ok(await identityProviderService.GetAsync(tenantId, providerName, cancellationToken)));

    [HttpPost]
    [RequestSizeLimit(MaxProviderBodyBytes)]
    [ProducesResponseType<TenantIdentityProviderResponse>(StatusCodes.Status201Created)]
    public Task<IActionResult> Create(
        [FromBody] CreateTenantIdentityProviderRequest request, CancellationToken cancellationToken) =>
        HandleAsync(async tenantId => StatusCode(
            StatusCodes.Status201Created, await identityProviderService.CreateAsync(tenantId, request, ActorSub, cancellationToken)));

    [HttpPut("{providerName}")]
    [RequestSizeLimit(MaxProviderBodyBytes)]
    [ProducesResponseType<TenantIdentityProviderResponse>(StatusCodes.Status200OK)]
    public Task<IActionResult> Update(
        string providerName, [FromBody] UpdateTenantIdentityProviderRequest request, CancellationToken cancellationToken) =>
        HandleAsync(async tenantId =>
            Ok(await identityProviderService.UpdateAsync(tenantId, providerName, request, ActorSub, cancellationToken)));

    [HttpPost("{providerName}/enable")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public Task<IActionResult> Enable(string providerName, CancellationToken cancellationToken) =>
        HandleAsync(async tenantId =>
        {
            await identityProviderService.SetEnabledAsync(tenantId, providerName, enabled: true, ActorSub, cancellationToken);
            return NoContent();
        });

    [HttpPost("{providerName}/disable")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public Task<IActionResult> Disable(string providerName, CancellationToken cancellationToken) =>
        HandleAsync(async tenantId =>
        {
            await identityProviderService.SetEnabledAsync(tenantId, providerName, enabled: false, ActorSub, cancellationToken);
            return NoContent();
        });

    [HttpDelete("{providerName}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public Task<IActionResult> Delete(string providerName, CancellationToken cancellationToken) =>
        HandleAsync(async tenantId =>
        {
            await identityProviderService.DeleteAsync(tenantId, providerName, ActorSub, cancellationToken);
            return NoContent();
        });

    // The verified caller RequireTenantRoleAttribute has already checked, recorded as the actor.
    private string ActorSub => HttpContext.Features.Get<AuthenticatedCaller>()?.PrincipalId
        ?? throw new InvalidOperationException("No authenticated caller; RequireTenantRoleAttribute must run first.");

    /// <summary>Parses the trusted tenant header, runs the action, and maps service exceptions to status codes.</summary>
    private async Task<IActionResult> HandleAsync(Func<Guid, Task<IActionResult>> action)
    {
        if (!Request.Headers.TryGetValue(TenantConstants.TenantHeaderName, out var tenantHeaderValue) ||
            !Guid.TryParse(tenantHeaderValue, out var tenantId))
        {
            return BadRequest($"Missing or invalid '{TenantConstants.TenantHeaderName}' header.");
        }

        try
        {
            return await action(tenantId);
        }
        catch (InvalidIdentityProviderException ex)
        {
            logger.LogWarning(ex, "Identity provider request rejected for tenant {TenantId}", tenantId);
            return BadRequest(ex.Message);
        }
        catch (IdentityProviderNotFoundException ex)
        {
            logger.LogWarning("Identity provider {ProviderName} not found for tenant {TenantId}", ex.ProviderName, tenantId);
            return NotFound(ex.Message);
        }
        catch (IdentityProviderConflictException ex)
        {
            logger.LogWarning("Identity provider {ProviderName} already exists for tenant {TenantId}", ex.ProviderName, tenantId);
            return Conflict(ex.Message);
        }
        catch (TenantNotFoundException)
        {
            logger.LogWarning("Identity provider request rejected: tenant {TenantId} not found", tenantId);
            return StatusCode(StatusCodes.Status403Forbidden);
        }
    }
}
