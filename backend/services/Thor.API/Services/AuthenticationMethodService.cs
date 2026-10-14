using Microsoft.EntityFrameworkCore;
using Thor.Api.Exceptions;
using Thor.Api.Models;
using Thor.DataConnectionManager;
using Thor.DataLayer.Data;
using Thor.DataLayer.Models;
using Thor.DataLayer.Models.Tenants;
using Thor.DataLayer.Repositories;

namespace Thor.Api.Services;

/// <summary>
/// Backs <c>POST v{version}/authentication-methods</c>: validates the requested authentication type and
/// its fields against the Master metadata DB, writes each field's secret value to Secrets
/// Manager (see docs/architecture/ADR-CONNECTOR-CREDENTIAL-MANAGEMENT.md), then persists a named
/// <see cref="AuthenticationMethod"/> plus its <see cref="AuthenticationValue"/>s (secret ARN
/// only, no plaintext) in the caller's tenant database. Also backs
/// <c>GET v{version}/authentication-methods/types</c>: lists the <see cref="AuthenticationType"/>
/// reference rows from the Master metadata DB, optionally only those supported by one connector type,
/// and <c>GET v{version}/authentication-methods/types/{typeId}/fields</c>: lists the
/// <see cref="AuthenticationField"/>s a given authentication type requires. Also backs
/// <c>GET</c>/<c>PUT v{version}/authentication-methods/{id}</c>: reads back (with secrets
/// redacted) and updates an existing method.
/// </summary>
public sealed class AuthenticationMethodService(
    ITenantConnectionManager tenantConnectionManager,
    IMasterDbContextFactory masterDbContextFactory,
    MasterConnectionInfo masterConnectionInfo,
    IAuthenticationSecretWriter authenticationSecretWriter,
    ILogger<AuthenticationMethodService> logger)
{
    public async Task<AuthenticationMethodResponse> CreateAsync(
        Guid tenantId, string actorId, CreateAuthenticationMethodRequest request, CancellationToken cancellationToken)
    {
        using var masterDb = masterDbContextFactory.Create(masterConnectionInfo);
        var typeRepository = new AuthenticationTypeRepository(masterDb);

        var type = await typeRepository.GetByIdAsync(request.TypeId, cancellationToken)
            ?? throw new AuthenticationTypeNotFoundException(request.TypeId);

        var fieldRepository = new AuthenticationFieldRepository(masterDb);
        var fields = await fieldRepository.GetByTypeIdAsync(type.Id, cancellationToken);

        ValidateFields(fields, request.Values);
        ValidateFileValues(fields, request.Values);

        using var tenantDb = await tenantConnectionManager.GetTenantDbContextAsync(tenantId, cancellationToken);
        var methodRepository = new AuthenticationMethodRepository(tenantDb);

        var secretName = AuthenticationSecretNaming.SecretName(tenantId, type.Id);

        await using var transaction = await tenantDb.Database.BeginTransactionAsync(cancellationToken);

        // Postgres advisory lock, scoped to this tenant+authentication-type secret, held for
        // the lifetime of this transaction and released automatically on commit or rollback.
        // Serializes the Secrets Manager read-modify-write below across every caller — including
        // a different Thor.Api/ECS task — closing the race an in-process-only lock could not
        // (see docs/architecture/ADR-CONNECTOR-CREDENTIAL-MANAGEMENT.md).
        await tenantDb.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtext({secretName}))", cancellationToken);

        logger.LogDebug("Acquired advisory lock for secret {SecretName}", secretName);

        var now = DateTimeOffset.UtcNow;
        var method = new AuthenticationMethod
        {
            Id = Guid.NewGuid(),
            TypeId = request.TypeId,
            Name = request.Name,
            Description = request.Description,
        };

        var valuesByRowId = new Dictionary<Guid, string>();

        foreach (var value in request.Values)
        {
            var valueId = Guid.NewGuid();
            valuesByRowId[valueId] = value.Value;

            method.AuthenticationValues.Add(new AuthenticationValue
            {
                Id = valueId,
                MethodId = method.Id,
                FieldId = value.FieldId,
                FileName = value.FileName,
                CreatedAt = now,
                UpdatedAt = now,
                CreatedBy = actorId,
                UpdatedBy = actorId,
            });
        }

        // One request has exactly one authentication type, so every field value here belongs
        // to the same tenant+type secret — write them all in a single get-then-put rather than
        // one round trip per field (see IAuthenticationSecretWriter).
        var secretArn = await authenticationSecretWriter.StoreValuesAsync(
            tenantId, type.Id, valuesByRowId, cancellationToken);

        logger.LogInformation("Stored authentication secret at {SecretArn}", secretArn);

        foreach (var authenticationValue in method.AuthenticationValues)
        {
            authenticationValue.SecretArn = secretArn;
        }

        await methodRepository.AddAsync(method, cancellationToken);
        await tenantDb.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        logger.LogInformation(
            "Persisted authentication method {AuthenticationMethodId} with {ValueCount} value(s)",
            method.Id, method.AuthenticationValues.Count);

        return new AuthenticationMethodResponse(
            method.Id,
            method.TypeId,
            method.Name,
            method.Description,
            method.AuthenticationValues.Select(v => v.Id).ToList());
    }

    /// <summary>
    /// Updates an existing method's Name/Description/Type/values in place. Values are validated
    /// against the *target* type's fields and written onto the same <see cref="AuthenticationValue"/>
    /// rows (matched by FieldId), so their secret entries are overwritten rather than duplicated.
    /// When the type changes, rows for fields the new type lacks are deleted, and each of the new
    /// type's fields gets a fresh row.
    /// </summary>
    public async Task<AuthenticationMethodResponse> UpdateAsync(
        Guid tenantId, string actorId, Guid methodId, UpdateAuthenticationMethodRequest request, CancellationToken cancellationToken)
    {
        using var tenantDb = await tenantConnectionManager.GetTenantDbContextAsync(tenantId, cancellationToken);

        var method = await tenantDb.AuthenticationMethods
            .Include(m => m.AuthenticationValues)
            .FirstOrDefaultAsync(m => m.Id == methodId, cancellationToken)
            ?? throw new AuthenticationMethodNotFoundException(methodId);

        using var masterDb = masterDbContextFactory.Create(masterConnectionInfo);
        var typeRepository = new AuthenticationTypeRepository(masterDb);
        var type = await typeRepository.GetByIdAsync(request.TypeId, cancellationToken)
            ?? throw new AuthenticationTypeNotFoundException(request.TypeId);

        var fieldRepository = new AuthenticationFieldRepository(masterDb);
        var fields = await fieldRepository.GetByTypeIdAsync(type.Id, cancellationToken);

        ValidateFields(fields, request.Values);
        ValidateFileValues(fields, request.Values);

        var secretName = AuthenticationSecretNaming.SecretName(tenantId, type.Id);

        await using var transaction = await tenantDb.Database.BeginTransactionAsync(cancellationToken);

        // Same advisory lock as CreateAsync, on the *target* type's secret (the one written below).
        await tenantDb.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtext({secretName}))", cancellationToken);

        logger.LogDebug("Acquired advisory lock for secret {SecretName}", secretName);

        var now = DateTimeOffset.UtcNow;
        method.Name = request.Name;
        method.Description = request.Description;

        // Rows for fields the target type doesn't have (none unless the type is changing).
        // TODO: their entries stay behind, unread, in the old type's secret -
        // IAuthenticationSecretWriter has no delete operation yet.
        var targetFieldIds = fields.Select(f => f.Id).ToHashSet();
        var staleValues = method.AuthenticationValues.Where(v => !targetFieldIds.Contains(v.FieldId)).ToList();
        foreach (var stale in staleValues)
        {
            method.AuthenticationValues.Remove(stale);
        }
        tenantDb.AuthenticationValues.RemoveRange(staleValues);

        if (staleValues.Count > 0)
        {
            logger.LogInformation(
                "Removed {StaleValueCount} value(s) of authentication method {AuthenticationMethodId} not in target type {AuthenticationTypeId}",
                staleValues.Count, method.Id, type.Id);
        }

        method.TypeId = type.Id;

        var fieldsById = fields.ToDictionary(f => f.Id);
        var existingValuesByFieldId = method.AuthenticationValues.ToDictionary(v => v.FieldId);
        var valuesByRowId = new Dictionary<Guid, string>();

        foreach (var value in request.Values)
        {
            // An edit form never re-displays a stored password or file, so a blank one means
            // "leave unchanged", not "clear it". Only applies when a row already exists; a new
            // field (e.g. after a type switch) is written as submitted, like CreateAsync.
            var isBlankSecretField = string.IsNullOrEmpty(value.Value) &&
                existingValuesByFieldId.ContainsKey(value.FieldId) &&
                IsSecretField(fieldsById[value.FieldId]);
            if (isBlankSecretField)
            {
                continue;
            }

            if (!existingValuesByFieldId.TryGetValue(value.FieldId, out var existing))
            {
                existing = new AuthenticationValue
                {
                    Id = Guid.NewGuid(),
                    MethodId = method.Id,
                    FieldId = value.FieldId,
                    CreatedAt = now,
                    CreatedBy = actorId,
                };
                // Added explicitly: EF would track a discovered entity with a preset Guid key as
                // Modified. Fix-up adds it to method.AuthenticationValues.
                tenantDb.AuthenticationValues.Add(existing);
            }

            existing.FileName = value.FileName;
            existing.UpdatedAt = now;
            existing.UpdatedBy = actorId;
            valuesByRowId[existing.Id] = value.Value;
        }

        var secretArn = await authenticationSecretWriter.StoreValuesAsync(
            tenantId, type.Id, valuesByRowId, cancellationToken);

        logger.LogInformation("Stored authentication secret at {SecretArn}", secretArn);

        foreach (var authenticationValue in method.AuthenticationValues)
        {
            authenticationValue.SecretArn = secretArn;
        }

        await tenantDb.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        logger.LogInformation(
            "Updated authentication method {AuthenticationMethodId}: wrote {WrittenValueCount} of {ValueCount} value(s)",
            method.Id, valuesByRowId.Count, method.AuthenticationValues.Count);

        return new AuthenticationMethodResponse(
            method.Id,
            method.TypeId,
            method.Name,
            method.Description,
            method.AuthenticationValues.Select(v => v.Id).ToList());
    }

    // Stands in for a stored password-family value in GetValuesAsync; callers only check that
    // it's present, never read it.
    private const string PasswordFieldPlaceholder = "********";

    public async Task<AuthenticationMethodValuesResponse> GetValuesAsync(
        Guid tenantId, Guid methodId, CancellationToken cancellationToken)
    {
        using var tenantDb = await tenantConnectionManager.GetTenantDbContextAsync(tenantId, cancellationToken);

        var method = await tenantDb.AuthenticationMethods
            .Include(m => m.AuthenticationValues)
            .FirstOrDefaultAsync(m => m.Id == methodId, cancellationToken)
            ?? throw new AuthenticationMethodNotFoundException(methodId);

        using var masterDb = masterDbContextFactory.Create(masterConnectionInfo);
        var fieldRepository = new AuthenticationFieldRepository(masterDb);
        var fieldsById = (await fieldRepository.GetByTypeIdAsync(method.TypeId, cancellationToken))
            .ToDictionary(f => f.Id);

        var secretValues = await authenticationSecretWriter.GetValuesAsync(tenantId, method.TypeId, cancellationToken);

        var values = new Dictionary<string, string>();
        foreach (var authenticationValue in method.AuthenticationValues)
        {
            if (!fieldsById.TryGetValue(authenticationValue.FieldId, out var field))
            {
                continue;
            }

            // A file field shows its stored filename (rows saved without one are omitted),
            // never the file content.
            if (IsFileField(field))
            {
                if (!string.IsNullOrEmpty(authenticationValue.FileName))
                {
                    values[field.Name] = authenticationValue.FileName;
                }
                continue;
            }

            if (secretValues.TryGetValue(authenticationValue.Id, out var value) && !string.IsNullOrEmpty(value))
            {
                values[field.Name] = IsPasswordField(field) ? PasswordFieldPlaceholder : value;
            }
        }

        logger.LogDebug(
            "Read {ValueCount} value(s) for authentication method {AuthenticationMethodId}",
            values.Count, method.Id);

        return new AuthenticationMethodValuesResponse(method.Id, method.TypeId, method.Name, values);
    }

    public async Task<IReadOnlyList<AuthenticationTypeResponse>> ListTypesAsync(short? connectorType, CancellationToken cancellationToken)
    {
        using var masterDb = masterDbContextFactory.Create(masterConnectionInfo);
        var typeRepository = new AuthenticationTypeRepository(masterDb);

        var types = connectorType is { } filter
            ? await typeRepository.GetByConnectorTypeAsync(filter, cancellationToken)
            : await typeRepository.GetAllAsync(cancellationToken);

        // Authentication types are deployment seed data, so an empty unfiltered table means the
        // Master DB seed hasn't run (or failed) rather than a normal state. A filtered result can
        // legitimately be empty (the connector has no mapped types yet).
        if (types.Count == 0 && connectorType is null)
        {
            logger.LogWarning("No authentication types found in the Master DB; deployment seed may be missing");
        }
        else
        {
            logger.LogDebug("Listed {AuthenticationTypeCount} authentication type(s) for connector type {ConnectorType}", types.Count, connectorType);
        }

        return types
            .OrderBy(t => t.Name, StringComparer.Ordinal)
            .Select(t => new AuthenticationTypeResponse(t.Id, t.Name))
            .ToList();
    }

    public async Task<IReadOnlyList<AuthenticationFieldResponse>> ListFieldsAsync(Guid typeId, CancellationToken cancellationToken)
    {
        using var masterDb = masterDbContextFactory.Create(masterConnectionInfo);
        var typeRepository = new AuthenticationTypeRepository(masterDb);

        // Distinguish an unknown type (404) from a known type that defines no fields (empty list).
        _ = await typeRepository.GetByIdAsync(typeId, cancellationToken)
            ?? throw new AuthenticationTypeNotFoundException(typeId);

        var fieldRepository = new AuthenticationFieldRepository(masterDb);
        var fields = await fieldRepository.GetByTypeIdAsync(typeId, cancellationToken);

        logger.LogDebug("Listed {AuthenticationFieldCount} authentication field(s) for authentication type {AuthenticationTypeId}", fields.Count, typeId);

        return fields
            .OrderBy(f => f.DisplayOrder)
            .Select(f => new AuthenticationFieldResponse(f.Id, f.Name, f.DisplayName, f.InputType, f.Description))
            .ToList();
    }

    private static void ValidateFields(IReadOnlyList<AuthenticationField> fields, IReadOnlyList<AuthenticationValueRequest> values)
    {
        var requiredFieldIds = fields.Select(f => f.Id).ToHashSet();
        var submittedFieldIds = values.Select(v => v.FieldId).ToHashSet();

        var missing = requiredFieldIds.Except(submittedFieldIds).ToList();
        var unknown = submittedFieldIds.Except(requiredFieldIds).ToList();

        if (missing.Count > 0 || unknown.Count > 0)
        {
            throw new InvalidAuthenticationFieldsException(missing, unknown);
        }
    }

    // Runs after ValidateFields, so every value's FieldId is known. A FileName means the value
    // came from an uploaded file part, which only file-family fields accept.
    private static void ValidateFileValues(IReadOnlyList<AuthenticationField> fields, IReadOnlyList<AuthenticationValueRequest> values)
    {
        var fieldsById = fields.ToDictionary(f => f.Id);
        var invalid = values.FirstOrDefault(v => v.FileName is not null && !IsFileField(fieldsById[v.FieldId]));
        if (invalid is not null)
        {
            throw new InvalidAuthenticationFileFieldException(invalid.FieldId);
        }
    }

    // InputType is a comma-separated list (e.g. "radio,password"); "certificate-password" also
    // counts as a password.
    private static bool IsPasswordField(AuthenticationField field) => field.InputType.Contains("password");

    private static bool IsFileField(AuthenticationField field) => field.InputType.Contains("file");

    private static bool IsSecretField(AuthenticationField field) => IsFileField(field) || IsPasswordField(field);
}
