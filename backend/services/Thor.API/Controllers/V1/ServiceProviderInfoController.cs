using Asp.Versioning;
using Microsoft.AspNetCore.Mvc;
using Thor.Api.Constants;
using Thor.Api.Exceptions;
using Thor.Api.Filters;
using Thor.Api.Models;
using Thor.Api.Services;
using Thor.DataConnectionManager.Exceptions;

namespace Thor.Api.Controllers.V1;

/// <summary>
/// What a tenant admin needs to register Thor in their own IdP before adding it under
/// <c>identity-providers</c> (see <see cref="ServiceProviderInfoService"/>). Its own route so it
/// can never shadow an IdP name.
/// </summary>
[ApiController]
[ApiVersion("1.0")]
[Route("v{version:apiVersion}/sso/service-provider")]
[RequireTenantRole(TenantRole.Administrator)]
public class ServiceProviderInfoController(
    ServiceProviderInfoService serviceProviderInfoService,
    ILogger<ServiceProviderInfoController> logger) : ControllerBase
{
    // X-THOR-TENANT-ID is set by API Gateway from the Lambda authorizer's verified context, so it's
    // read directly rather than re-verified here.
    [HttpGet]
    [ProducesResponseType<ServiceProviderInfoResponse>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Get(CancellationToken cancellationToken)
    {
        if (!Request.Headers.TryGetValue(TenantConstants.TenantHeaderName, out var tenantHeaderValue) ||
            !Guid.TryParse(tenantHeaderValue, out var tenantId))
        {
            return BadRequest($"Missing or invalid '{TenantConstants.TenantHeaderName}' header.");
        }

        try
        {
            return Ok(await serviceProviderInfoService.GetAsync(tenantId, cancellationToken));
        }
        catch (SsoNotConfiguredException ex)
        {
            logger.LogWarning("Service provider info requested before SSO setup for tenant {TenantId}", tenantId);
            return Conflict(ex.Message);
        }
        catch (TenantNotFoundException)
        {
            logger.LogWarning("Service provider info request rejected: tenant {TenantId} not found", tenantId);
            return StatusCode(StatusCodes.Status403Forbidden);
        }
    }
}
