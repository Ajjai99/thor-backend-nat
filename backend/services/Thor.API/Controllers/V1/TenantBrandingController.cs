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
/// Tenant-admin management of the logo shown on the tenant's login page (see
/// <see cref="TenantBrandingService"/>): get an upload URL, PUT the file to it, then publish it.
/// </summary>
[ApiController]
[ApiVersion("1.0")]
[Route("v{version:apiVersion}/branding/logo")]
[RequireTenantRole(TenantRole.Administrator)]
public class TenantBrandingController(
    TenantBrandingService tenantBrandingService,
    ILogger<TenantBrandingController> logger) : ControllerBase
{
    // X-THOR-TENANT-ID is set by API Gateway from the Lambda authorizer's verified context, so it's
    // read directly rather than re-verified here.
    [HttpPost("upload-url")]
    [ProducesResponseType<LogoUploadResponse>(StatusCodes.Status200OK)]
    public Task<IActionResult> CreateUpload([FromBody] CreateLogoUploadRequest request, CancellationToken cancellationToken) =>
        HandleAsync(async tenantId => Ok(await tenantBrandingService.CreateUploadAsync(tenantId, request, cancellationToken)));

    [HttpPut]
    [ProducesResponseType<LogoResponse>(StatusCodes.Status200OK)]
    public Task<IActionResult> Publish([FromBody] PublishLogoRequest request, CancellationToken cancellationToken) =>
        HandleAsync(async tenantId => Ok(await tenantBrandingService.PublishAsync(tenantId, request, cancellationToken)));

    [HttpDelete]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public Task<IActionResult> Delete(CancellationToken cancellationToken) =>
        HandleAsync(async tenantId =>
        {
            await tenantBrandingService.DeleteAsync(tenantId, cancellationToken);
            return NoContent();
        });

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
        catch (InvalidLogoException ex)
        {
            logger.LogWarning(ex, "Logo request rejected for tenant {TenantId}", tenantId);
            return BadRequest(ex.Message);
        }
        catch (LogoNotFoundException ex)
        {
            logger.LogWarning("No logo to remove for tenant {TenantId}", tenantId);
            return NotFound(ex.Message);
        }
        catch (TenantNotFoundException)
        {
            logger.LogWarning("Logo request rejected: tenant {TenantId} not found", tenantId);
            return StatusCode(StatusCodes.Status403Forbidden);
        }
    }
}
