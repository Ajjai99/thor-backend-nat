using System.Text.Json;
using Asp.Versioning;
using Microsoft.AspNetCore.Mvc;
using Thor.Api.Constants;
using Thor.Api.Exceptions;
using Thor.Api.Models;
using Thor.Api.Services;
using Thor.DataConnectionManager.Exceptions;

namespace Thor.Api.Controllers.V1;

[ApiController]
[ApiVersion("1.0")]
[Route("v{version:apiVersion}/authentication-methods")]
public class AuthenticationMethodsController(
    AuthenticationMethodService authenticationMethodService,
    ILogger<AuthenticationMethodsController> logger) : ControllerBase
{
    // Every value for a tenant + authentication type shares one Secrets Manager secret (64 KB
    // limit) and files are stored base64-encoded (~4/3 larger), so uploads are capped well below
    // that. Typical PEM keys and certificates are 2-10 KB.
    private const long MaxFileBytes = 16 * 1024;

    private static readonly JsonSerializerOptions RequestJsonOptions = new(JsonSerializerDefaults.Web);

    // The Lambda authorizer validates the caller and sets X-THOR-TENANT-ID / X-THOR-ACTOR-ID;
    // requests reaching Thor.Api are trusted, so the headers are read directly rather than
    // re-verified here.
    //
    // multipart/form-data, not JSON, because a file-type field's value is an uploaded file. The
    // CreateAuthenticationMethodRequest travels as a JSON string in the `request` form field;
    // each file-type field's file is a form file named by that field's FieldId, and replaces
    // that field's Value (see MergeFileValuesAsync).
    [HttpPost]
    [Consumes("multipart/form-data")]
    public async Task<ActionResult<AuthenticationMethodResponse>> Create(
        [FromForm] string request, CancellationToken cancellationToken)
    {
        if (!Request.Headers.TryGetValue(TenantConstants.TenantHeaderName, out var tenantHeaderValue) ||
            !Guid.TryParse(tenantHeaderValue, out var tenantId))
        {
            return BadRequest($"Missing or invalid '{TenantConstants.TenantHeaderName}' header.");
        }

        if (!Request.Headers.TryGetValue(TenantConstants.ActorHeaderName, out var actorHeaderValue) ||
            string.IsNullOrWhiteSpace(actorHeaderValue))
        {
            return BadRequest($"Missing '{TenantConstants.ActorHeaderName}' header.");
        }

        var parsedRequest = DeserializeRequest<CreateAuthenticationMethodRequest>(request);
        if (parsedRequest?.Values is null)
        {
            return BadRequest("Invalid 'request' payload.");
        }

        if (string.IsNullOrWhiteSpace(parsedRequest.Name))
        {
            return BadRequest("Name is required.");
        }

        if (parsedRequest.Values.Count == 0)
        {
            return BadRequest("At least one authentication value is required.");
        }

        var (mergedValues, fileError) = await MergeFileValuesAsync(parsedRequest.Values, cancellationToken);
        if (fileError is not null)
        {
            return BadRequest(fileError);
        }
        parsedRequest = parsedRequest with { Values = mergedValues };

        try
        {
            var response = await authenticationMethodService.CreateAsync(
                tenantId, actorHeaderValue!, parsedRequest, cancellationToken);
            return StatusCode(StatusCodes.Status201Created, response);
        }
        catch (AuthenticationTypeNotFoundException ex)
        {
            logger.LogWarning(ex, "Authentication method creation rejected: authentication type not found");
            return BadRequest(ex.Message);
        }
        catch (InvalidAuthenticationFieldsException ex)
        {
            logger.LogWarning(ex, "Authentication method creation rejected: invalid authentication fields");
            return BadRequest(ex.Message);
        }
        catch (InvalidAuthenticationFileFieldException ex)
        {
            logger.LogWarning(ex, "Authentication method creation rejected: file uploaded for non-file field {FieldId}", ex.FieldId);
            return BadRequest(ex.Message);
        }
        catch (TenantNotFoundException)
        {
            logger.LogWarning("Authentication method creation rejected: tenant {TenantId} not found", tenantId);
            return StatusCode(StatusCodes.Status403Forbidden);
        }
    }

    // Tenant-scoped read, so X-THOR-TENANT-ID is required; nothing is written, so no actor is.
    // Password values are never returned (see AuthenticationMethodValuesResponse).
    [HttpGet("{id:guid}")]
    public async Task<ActionResult<AuthenticationMethodValuesResponse>> GetValues(
        Guid id, CancellationToken cancellationToken)
    {
        if (!Request.Headers.TryGetValue(TenantConstants.TenantHeaderName, out var tenantHeaderValue) ||
            !Guid.TryParse(tenantHeaderValue, out var tenantId))
        {
            return BadRequest($"Missing or invalid '{TenantConstants.TenantHeaderName}' header.");
        }

        try
        {
            var response = await authenticationMethodService.GetValuesAsync(tenantId, id, cancellationToken);
            return Ok(response);
        }
        catch (AuthenticationMethodNotFoundException ex)
        {
            logger.LogWarning(ex, "Authentication method read rejected: method {AuthenticationMethodId} not found", id);
            return NotFound(ex.Message);
        }
        catch (TenantNotFoundException)
        {
            logger.LogWarning("Authentication method read rejected: tenant {TenantId} not found", tenantId);
            return StatusCode(StatusCodes.Status403Forbidden);
        }
    }

    // Same multipart shape as Create, carrying an UpdateAuthenticationMethodRequest. A blank
    // password/file value leaves the stored one unchanged (see AuthenticationMethodService.UpdateAsync).
    [HttpPut("{id:guid}")]
    [Consumes("multipart/form-data")]
    public async Task<ActionResult<AuthenticationMethodResponse>> Update(
        Guid id, [FromForm] string request, CancellationToken cancellationToken)
    {
        if (!Request.Headers.TryGetValue(TenantConstants.TenantHeaderName, out var tenantHeaderValue) ||
            !Guid.TryParse(tenantHeaderValue, out var tenantId))
        {
            return BadRequest($"Missing or invalid '{TenantConstants.TenantHeaderName}' header.");
        }

        if (!Request.Headers.TryGetValue(TenantConstants.ActorHeaderName, out var actorHeaderValue) ||
            string.IsNullOrWhiteSpace(actorHeaderValue))
        {
            return BadRequest($"Missing '{TenantConstants.ActorHeaderName}' header.");
        }

        var parsedRequest = DeserializeRequest<UpdateAuthenticationMethodRequest>(request);
        if (parsedRequest?.Values is null)
        {
            return BadRequest("Invalid 'request' payload.");
        }

        if (string.IsNullOrWhiteSpace(parsedRequest.Name))
        {
            return BadRequest("Name is required.");
        }

        if (parsedRequest.Values.Count == 0)
        {
            return BadRequest("At least one authentication value is required.");
        }

        var (mergedValues, fileError) = await MergeFileValuesAsync(parsedRequest.Values, cancellationToken);
        if (fileError is not null)
        {
            return BadRequest(fileError);
        }
        parsedRequest = parsedRequest with { Values = mergedValues };

        try
        {
            var response = await authenticationMethodService.UpdateAsync(
                tenantId, actorHeaderValue!, id, parsedRequest, cancellationToken);
            return Ok(response);
        }
        catch (AuthenticationMethodNotFoundException ex)
        {
            logger.LogWarning(ex, "Authentication method update rejected: method {AuthenticationMethodId} not found", id);
            return NotFound(ex.Message);
        }
        catch (AuthenticationTypeNotFoundException ex)
        {
            logger.LogWarning(ex, "Authentication method update rejected: authentication type not found");
            return BadRequest(ex.Message);
        }
        catch (InvalidAuthenticationFieldsException ex)
        {
            logger.LogWarning(ex, "Authentication method update rejected: invalid authentication fields");
            return BadRequest(ex.Message);
        }
        catch (InvalidAuthenticationFileFieldException ex)
        {
            logger.LogWarning(ex, "Authentication method update rejected: file uploaded for non-file field {FieldId}", ex.FieldId);
            return BadRequest(ex.Message);
        }
        catch (TenantNotFoundException)
        {
            logger.LogWarning("Authentication method update rejected: tenant {TenantId} not found", tenantId);
            return StatusCode(StatusCodes.Status403Forbidden);
        }
    }

    // Authentication types are Master-DB reference data shared by every tenant, so unlike Create
    // no X-THOR-TENANT-ID is needed; the Lambda authorizer still gates the route. Pass
    // ?connectorType= to list only the types that connector supports.
    [HttpGet("types")]
    public async Task<ActionResult<IReadOnlyList<AuthenticationTypeResponse>>> ListTypes(
        [FromQuery] short? connectorType, CancellationToken cancellationToken)
    {
        var response = await authenticationMethodService.ListTypesAsync(connectorType, cancellationToken);
        return Ok(response);
    }

    // Like ListTypes, authentication fields are Master-DB reference data, so no X-THOR-TENANT-ID
    // is needed. Returns the fields a caller must supply values for when creating an
    // authentication method of this type.
    [HttpGet("types/{typeId:guid}/fields")]
    public async Task<ActionResult<IReadOnlyList<AuthenticationFieldResponse>>> ListFields(
        Guid typeId, CancellationToken cancellationToken)
    {
        try
        {
            var response = await authenticationMethodService.ListFieldsAsync(typeId, cancellationToken);
            return Ok(response);
        }
        catch (AuthenticationTypeNotFoundException ex)
        {
            logger.LogWarning(ex, "Authentication field listing rejected: authentication type {AuthenticationTypeId} not found", typeId);
            return NotFound(ex.Message);
        }
    }

    private T? DeserializeRequest<T>(string request) where T : class
    {
        try
        {
            return JsonSerializer.Deserialize<T>(request, RequestJsonOptions);
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Authentication method request rejected: 'request' form field is not valid JSON");
            return null;
        }
    }

    /// <summary>
    /// Shared by Create/Update. Drops any FileName sent in the JSON (it must come from a real
    /// upload), then for each uploaded file (form file named by FieldId) replaces that field's
    /// Value with the file's content, base64-encoded, and sets FileName to the file's name.
    /// Whether the field accepts a file is checked later by the service, against its InputType.
    /// </summary>
    private async Task<(IReadOnlyList<AuthenticationValueRequest> Values, string? Error)> MergeFileValuesAsync(
        IReadOnlyList<AuthenticationValueRequest> values, CancellationToken cancellationToken)
    {
        var merged = values.Select(v => v with { FileName = null }).ToList();

        if (!Request.HasFormContentType)
        {
            return (merged, null);
        }

        foreach (var file in Request.Form.Files)
        {
            if (!Guid.TryParse(file.Name, out var fieldId))
            {
                logger.LogWarning("Authentication method request rejected: unexpected file part {FilePartName}", file.Name);
                return (values, $"Unexpected file part '{file.Name}': not a field id.");
            }

            if (file.Length > MaxFileBytes)
            {
                logger.LogWarning(
                    "Authentication method request rejected: file for field {FieldId} is {FileLength} bytes, over the {MaxFileBytes}-byte limit",
                    fieldId, file.Length, MaxFileBytes);
                return (values, $"File for field '{fieldId}' exceeds the {MaxFileBytes / 1024} KB limit.");
            }

            using var stream = new MemoryStream();
            await file.CopyToAsync(stream, cancellationToken);
            var fileValue = new AuthenticationValueRequest(fieldId, Convert.ToBase64String(stream.ToArray()), file.FileName);

            var index = merged.FindIndex(v => v.FieldId == fieldId);
            if (index >= 0)
            {
                merged[index] = fileValue;
            }
            else
            {
                merged.Add(fileValue);
            }
        }

        return (merged, null);
    }
}
