using System.Xml;
using Amazon.CognitoIdentityProvider;
using Amazon.CognitoIdentityProvider.Model;
using Microsoft.EntityFrameworkCore;
using Thor.Api.Constants;
using Thor.Api.Exceptions;
using Thor.Api.Models;
using Thor.Cognito;
using Thor.DataConnectionManager.Routing;
using Thor.DataLayer.Data;
using Thor.DataLayer.Models;

namespace Thor.Api.Services;

/// <summary>
/// Backs <c>v{version}/identity-providers</c>: manages SAML/OIDC identity providers on the tenant's
/// own Cognito user pool and enables them on its app client. The pool and app client come only
/// from the verified tenant's tenant_routing row, never from the request.
/// <para>
/// Cognito holds every provider setting and secret. Thor keeps only a <see cref="TenantIdentityProvider"/>
/// record per IdP: whether it's enabled, its vendor label and its SAML source. The OIDC client
/// secret is never logged or returned: responses are built from an allow-list of non-secret keys.
/// An update with a blank client secret keeps the one Cognito holds, so edit forms needn't
/// re-collect it. A SAML IdP entered field by field is turned into a metadata document
/// (<see cref="SamlMetadataBuilder"/>), since Cognito accepts only metadata.
/// Provider names can't be changed — Cognito keys federated users by
/// <c>{ProviderName}_{IdP subject}</c>, so a new name means new users.
/// </para>
/// <para>
/// An IdP is created disabled — off the app client, so nobody can sign in through it — and an
/// admin enables it once the IdP side is configured. Enabling and disabling write the app client
/// and Thor's record in the order that leaves a half-finished change disabled. An IdP with no
/// record (created before Thor kept them) counts as disabled. Every change is logged as an
/// <c>Audit</c> event naming the actor and the changed fields, never their values.
/// </para>
/// </summary>
public sealed class IdentityProviderService(
    IAmazonCognitoIdentityProvider cognito,
    ITenantRoutingResolver tenantRoutingResolver,
    IMasterDbContextFactory dbContextFactory,
    MasterConnectionInfo connectionInfo,
    TimeProvider timeProvider,
    ILogger<IdentityProviderService> logger)
{
    private const string OidcClientSecretKey = "client_secret";
    private const string EncryptedResponsesKey = "EncryptedResponses";
    private const string RequestSigningAlgorithmKey = "RequestSigningAlgorithm";
    private const string RequestSigningAlgorithm = "rsa-sha256";
    private const string ActiveEncryptionCertificateKey = "ActiveEncryptionCertificate";

    /// <summary>
    /// The user-pool attributes an IdP claim may be mapped to: profile attributes only, so an IdP
    /// can't set verification flags, recovery contacts or custom attributes. <c>username</c> is
    /// Cognito's own mapping on OIDC IdPs (to <c>sub</c>), which Cognito restricts itself.
    /// </summary>
    private static readonly IReadOnlyList<string> MappableAttributes = ["email", "given_name", "family_name", "name", "username"];

    /// <summary>
    /// Cognito's local-account provider. Always kept on the app client, and never manageable
    /// through this API, so a misconfigured IdP can't lock the tenant's local admins out.
    /// </summary>
    public const string CognitoProvider = "COGNITO";

    private const int ListPageSize = 60;

    public async Task<IReadOnlyList<TenantIdentityProviderSummaryResponse>> ListAsync(
        Guid tenantId, CancellationToken cancellationToken)
    {
        var routing = await tenantRoutingResolver.ResolveAsync(tenantId, cancellationToken);

        Dictionary<string, TenantIdentityProvider> records;
        await using (var db = dbContextFactory.Create(connectionInfo))
        {
            records = await db.TenantIdentityProviders
                .Where(p => p.TenantId == tenantId)
                .ToDictionaryAsync(p => p.ProviderName, StringComparer.Ordinal, cancellationToken);
        }

        var providers = new List<TenantIdentityProviderSummaryResponse>();
        string? nextToken = null;
        do
        {
            var page = await cognito.ListIdentityProvidersAsync(
                new ListIdentityProvidersRequest
                {
                    UserPoolId = routing.UserPoolId,
                    MaxResults = ListPageSize,
                    NextToken = nextToken,
                },
                cancellationToken);

            foreach (var provider in page.Providers ?? [])
            {
                var record = records.GetValueOrDefault(provider.ProviderName);
                providers.Add(new TenantIdentityProviderSummaryResponse(
                    provider.ProviderName, provider.ProviderType?.Value ?? string.Empty,
                    provider.CreationDate, provider.LastModifiedDate,
                    record?.Enabled ?? false, record?.Vendor));
            }

            nextToken = page.NextToken;
        }
        while (!string.IsNullOrEmpty(nextToken));

        return providers;
    }

    public async Task<TenantIdentityProviderResponse> GetAsync(
        Guid tenantId, string providerName, CancellationToken cancellationToken)
    {
        var routing = await tenantRoutingResolver.ResolveAsync(tenantId, cancellationToken);
        var provider = await DescribeAsync(routing.UserPoolId, providerName, cancellationToken);
        var record = await FindRecordAsync(tenantId, providerName, cancellationToken);
        return ToResponse(provider, record, tenantId);
    }

    public async Task<TenantIdentityProviderResponse> CreateAsync(
        Guid tenantId, CreateTenantIdentityProviderRequest request, string actorSub, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.ProviderName))
        {
            throw new InvalidIdentityProviderException("ProviderName is required.");
        }

        EnsureNotReserved(request.ProviderName);
        EnsureKnownVendor(request.Vendor);
        EnsureMappable(request.AttributeMapping, current: null);
        var (providerType, providerDetails, samlSource) = BuildProviderDetails(
            request.ProviderType, request.Saml, request.Oidc, current: null);

        var routing = await tenantRoutingResolver.ResolveAsync(tenantId, cancellationToken);

        IdentityProviderType created;
        try
        {
            var response = await cognito.CreateIdentityProviderAsync(
                new CreateIdentityProviderRequest
                {
                    UserPoolId = routing.UserPoolId,
                    ProviderName = request.ProviderName,
                    ProviderType = providerType,
                    ProviderDetails = providerDetails,
                    AttributeMapping = request.AttributeMapping,
                    IdpIdentifiers = request.IdpIdentifiers,
                },
                cancellationToken);
            created = response.IdentityProvider;
        }
        catch (DuplicateProviderException)
        {
            throw new IdentityProviderConflictException(request.ProviderName);
        }
        catch (InvalidParameterException ex)
        {
            throw new InvalidIdentityProviderException(ex.Message, ex);
        }

        TenantIdentityProvider record;
        try
        {
            record = await InsertRecordAsync(tenantId, request.ProviderName, request.Vendor, samlSource, actorSub, cancellationToken);
        }
        catch (Exception ex)
        {
            // Without a record the IdP could never be enabled, and leaving it behind would turn the
            // caller's retry into a 409 — so undo the create.
            logger.LogError(ex, "Recording identity provider {ProviderName} failed; deleting it", request.ProviderName);
            await DeleteProviderBestEffortAsync(routing.UserPoolId, request.ProviderName);
            throw;
        }

        Audit("identity_provider.created", tenantId, request.ProviderName, actorSub, []);
        return ToResponse(created, record, tenantId);
    }

    public async Task<TenantIdentityProviderResponse> UpdateAsync(
        Guid tenantId, string providerName, UpdateTenantIdentityProviderRequest request, string actorSub,
        CancellationToken cancellationToken)
    {
        EnsureKnownVendor(request.Vendor);

        var routing = await tenantRoutingResolver.ResolveAsync(tenantId, cancellationToken);
        var existing = await DescribeAsync(routing.UserPoolId, providerName, cancellationToken);

        // The current details are read only to carry over what the caller leaves out (the client
        // secret, SAML encryption and signing).
        var (providerType, providerDetails, samlSource) = BuildProviderDetails(
            request.ProviderType, request.Saml, request.Oidc, current: existing.ProviderDetails);

        if (existing.ProviderType != providerType)
        {
            throw new InvalidIdentityProviderException(
                $"ProviderType can't be changed (provider '{providerName}' is {existing.ProviderType?.Value}).");
        }

        EnsureMappable(request.AttributeMapping, current: existing.AttributeMapping);

        IdentityProviderType updated;
        try
        {
            var response = await cognito.UpdateIdentityProviderAsync(
                new UpdateIdentityProviderRequest
                {
                    UserPoolId = routing.UserPoolId,
                    ProviderName = providerName,
                    ProviderDetails = providerDetails,
                    AttributeMapping = request.AttributeMapping,
                    IdpIdentifiers = request.IdpIdentifiers,
                },
                cancellationToken);
            updated = response.IdentityProvider;
        }
        catch (ResourceNotFoundException)
        {
            throw new IdentityProviderNotFoundException(providerName);
        }
        catch (InvalidParameterException ex)
        {
            throw new InvalidIdentityProviderException(ex.Message, ex);
        }

        var changed = ChangedSettings(existing, providerDetails, request);
        var record = await SaveRecordAsync(tenantId, providerName, actorSub, r =>
        {
            if (request.Vendor is not null && request.Vendor != r.Vendor)
            {
                changed.Add("Vendor");
                r.Vendor = request.Vendor;
            }

            r.SamlMetadataSource = samlSource;
        }, cancellationToken);

        Audit("identity_provider.updated", tenantId, providerName, actorSub, changed);
        return ToResponse(updated, record, tenantId);
    }

    public async Task SetEnabledAsync(
        Guid tenantId, string providerName, bool enabled, string actorSub, CancellationToken cancellationToken)
    {
        var routing = await tenantRoutingResolver.ResolveAsync(tenantId, cancellationToken);

        // 404 before touching the app client or the record.
        _ = await DescribeAsync(routing.UserPoolId, providerName, cancellationToken);

        if (enabled)
        {
            // App client first, so the record never says enabled for an IdP users can't sign in with.
            await SetEnabledOnAppClientAsync(routing, providerName, enabled: true, cancellationToken);
            await SaveRecordAsync(tenantId, providerName, actorSub, r => r.Enabled = true, cancellationToken);
        }
        else
        {
            // Record first, so Thor treats the IdP as disabled even if the app-client step then fails.
            await SaveRecordAsync(tenantId, providerName, actorSub, r => r.Enabled = false, cancellationToken);
            await SetEnabledOnAppClientAsync(routing, providerName, enabled: false, cancellationToken);
        }

        Audit(enabled ? "identity_provider.enabled" : "identity_provider.disabled", tenantId, providerName, actorSub, []);
    }

    public async Task DeleteAsync(Guid tenantId, string providerName, string actorSub, CancellationToken cancellationToken)
    {
        var routing = await tenantRoutingResolver.ResolveAsync(tenantId, cancellationToken);

        // 404 before touching the app client.
        _ = await DescribeAsync(routing.UserPoolId, providerName, cancellationToken);

        // App client, then record, then the IdP itself: if a later step fails, what's left is a
        // disabled IdP, and a retry finishes the job (the earlier steps are no-ops by then).
        await SetEnabledOnAppClientAsync(routing, providerName, enabled: false, cancellationToken);
        await DeleteRecordAsync(tenantId, providerName, cancellationToken);

        try
        {
            await cognito.DeleteIdentityProviderAsync(
                new DeleteIdentityProviderRequest { UserPoolId = routing.UserPoolId, ProviderName = providerName },
                cancellationToken);
        }
        catch (ResourceNotFoundException)
        {
            throw new IdentityProviderNotFoundException(providerName);
        }

        Audit("identity_provider.deleted", tenantId, providerName, actorSub, []);
    }

    private async Task<IdentityProviderType> DescribeAsync(
        string userPoolId, string providerName, CancellationToken cancellationToken)
    {
        EnsureNotReserved(providerName);

        try
        {
            var response = await cognito.DescribeIdentityProviderAsync(
                new DescribeIdentityProviderRequest { UserPoolId = userPoolId, ProviderName = providerName },
                cancellationToken);
            return response.IdentityProvider;
        }
        catch (ResourceNotFoundException)
        {
            throw new IdentityProviderNotFoundException(providerName);
        }
        catch (InvalidParameterException ex)
        {
            throw new InvalidIdentityProviderException(ex.Message, ex);
        }
    }

    private async Task SetEnabledOnAppClientAsync(
        TenantRouting routing, string providerName, bool enabled, CancellationToken cancellationToken)
    {
        var described = await cognito.DescribeUserPoolClientAsync(
            new DescribeUserPoolClientRequest { UserPoolId = routing.UserPoolId, ClientId = routing.AppClientId },
            cancellationToken);
        var client = described.UserPoolClient;

        var current = client.SupportedIdentityProviders ?? [];
        var providers = current.Where(p => !string.Equals(p, providerName, StringComparison.Ordinal)).ToList();
        if (!providers.Contains(CognitoProvider, StringComparer.Ordinal))
        {
            providers.Insert(0, CognitoProvider);
        }

        if (enabled)
        {
            providers.Add(providerName);
        }

        if (providers.SequenceEqual(current, StringComparer.Ordinal))
        {
            return;
        }

        var update = UserPoolClientUpdateFactory.CopyOf(client);
        update.SupportedIdentityProviders = providers;
        await cognito.UpdateUserPoolClientAsync(update, cancellationToken);
    }

    private async Task<TenantIdentityProvider?> FindRecordAsync(
        Guid tenantId, string providerName, CancellationToken cancellationToken)
    {
        await using var db = dbContextFactory.Create(connectionInfo);
        return await db.TenantIdentityProviders.SingleOrDefaultAsync(
            p => p.TenantId == tenantId && p.ProviderName == providerName, cancellationToken);
    }

    private async Task<TenantIdentityProvider> InsertRecordAsync(
        Guid tenantId, string providerName, string? vendor, string? samlSource, string actorSub,
        CancellationToken cancellationToken)
    {
        await using var db = dbContextFactory.Create(connectionInfo);

        // Cognito just accepted the name as new, so any record under it belongs to an IdP deleted
        // outside Thor; it would otherwise block this insert.
        var stale = await db.TenantIdentityProviders.SingleOrDefaultAsync(
            p => p.TenantId == tenantId && p.ProviderName == providerName, cancellationToken);
        if (stale is not null)
        {
            db.TenantIdentityProviders.Remove(stale);
            await db.SaveChangesAsync(cancellationToken);
        }

        var now = timeProvider.GetUtcNow();
        var record = new TenantIdentityProvider
        {
            TenantId = tenantId,
            ProviderName = providerName,
            Vendor = vendor,
            SamlMetadataSource = samlSource,
            Enabled = false,
            CreatedBySub = actorSub,
            UpdatedBySub = actorSub,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.TenantIdentityProviders.Add(record);
        await db.SaveChangesAsync(cancellationToken);
        return record;
    }

    /// <summary>
    /// Applies <paramref name="change"/> to the IdP's record, first creating a disabled one if it has
    /// none (an IdP created before Thor kept records).
    /// </summary>
    private async Task<TenantIdentityProvider> SaveRecordAsync(
        Guid tenantId, string providerName, string actorSub, Action<TenantIdentityProvider> change,
        CancellationToken cancellationToken)
    {
        await using var db = dbContextFactory.Create(connectionInfo);
        var now = timeProvider.GetUtcNow();

        var record = await db.TenantIdentityProviders.SingleOrDefaultAsync(
            p => p.TenantId == tenantId && p.ProviderName == providerName, cancellationToken);
        if (record is null)
        {
            record = new TenantIdentityProvider
            {
                TenantId = tenantId,
                ProviderName = providerName,
                Enabled = false,
                CreatedBySub = actorSub,
                CreatedAt = now,
            };
            db.TenantIdentityProviders.Add(record);
        }

        change(record);
        record.UpdatedBySub = actorSub;
        record.UpdatedAt = now;
        await db.SaveChangesAsync(cancellationToken);
        return record;
    }

    private async Task DeleteRecordAsync(Guid tenantId, string providerName, CancellationToken cancellationToken)
    {
        await using var db = dbContextFactory.Create(connectionInfo);
        var record = await db.TenantIdentityProviders.SingleOrDefaultAsync(
            p => p.TenantId == tenantId && p.ProviderName == providerName, cancellationToken);
        if (record is not null)
        {
            db.TenantIdentityProviders.Remove(record);
            await db.SaveChangesAsync(cancellationToken);
        }
    }

    private async Task DeleteProviderBestEffortAsync(string userPoolId, string providerName)
    {
        try
        {
            // Not the request's token: the request may already be cancelled, and this must still run.
            await cognito.DeleteIdentityProviderAsync(
                new DeleteIdentityProviderRequest { UserPoolId = userPoolId, ProviderName = providerName },
                CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Rolling back identity provider {ProviderName} failed; it must be deleted manually", providerName);
        }
    }

    private void Audit(string auditEvent, Guid tenantId, string providerName, string actorSub, IReadOnlyList<string> changedFields) =>
        logger.LogInformation(
            "Audit {AuditEvent}: identity provider {ProviderName} of tenant {TenantId} by {ActorSub}, changed {ChangedFields}",
            auditEvent, providerName, tenantId, actorSub, changedFields);

    /// <summary>Names (never values) of the settings an update changes, for the audit event.</summary>
    private static List<string> ChangedSettings(
        IdentityProviderType existing, Dictionary<string, string> providerDetails, UpdateTenantIdentityProviderRequest request)
    {
        var current = existing.ProviderDetails ?? new Dictionary<string, string>();
        var changed = providerDetails
            .Where(d => current.GetValueOrDefault(d.Key) != d.Value)
            .Select(d => d.Key)
            .ToList();

        var currentMapping = existing.AttributeMapping ?? new Dictionary<string, string>();
        if (request.AttributeMapping is { } mapping &&
            (mapping.Count != currentMapping.Count || mapping.Any(m => currentMapping.GetValueOrDefault(m.Key) != m.Value)))
        {
            changed.Add("AttributeMapping");
        }

        if (request.IdpIdentifiers is { } identifiers && !identifiers.SequenceEqual(existing.IdpIdentifiers ?? []))
        {
            changed.Add("IdpIdentifiers");
        }

        return changed;
    }

    private static void EnsureNotReserved(string providerName)
    {
        if (string.Equals(providerName, CognitoProvider, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidIdentityProviderException($"'{CognitoProvider}' is reserved for local accounts.");
        }
    }

    private static void EnsureKnownVendor(string? vendor)
    {
        if (vendor is not null && !IdentityProviderVendors.All.Contains(vendor, StringComparer.Ordinal))
        {
            throw new InvalidIdentityProviderException(
                $"vendor must be one of {string.Join(", ", IdentityProviderVendors.All)}.");
        }
    }

    /// <summary>
    /// Rejects a new or changed mapping into anything but <see cref="MappableAttributes"/>. A mapping
    /// the IdP already has passes unchanged, so an edit form that sends back what it loaded saves.
    /// </summary>
    private static void EnsureMappable(Dictionary<string, string>? requested, Dictionary<string, string>? current)
    {
        var rejected = (requested ?? new Dictionary<string, string>())
            .Where(m => !MappableAttributes.Contains(m.Key, StringComparer.Ordinal) &&
                        current?.GetValueOrDefault(m.Key) != m.Value)
            .Select(m => m.Key)
            .ToList();

        if (rejected.Count > 0)
        {
            throw new InvalidIdentityProviderException(
                $"IdP attributes can't be mapped to {string.Join(", ", rejected)}; " +
                $"allowed user pool attributes are {string.Join(", ", MappableAttributes)}.");
        }
    }

    /// <param name="current">
    /// The provider details Cognito already holds (update only). The OIDC secret is taken from them
    /// when the request leaves it blank, and SAML encryption/signing when the request omits them.
    /// </param>
    private (IdentityProviderTypeType Type, Dictionary<string, string> Details, string? SamlSource) BuildProviderDetails(
        string? providerType, SamlProviderSettings? saml, OidcProviderSettings? oidc, Dictionary<string, string>? current)
    {
        if (string.Equals(providerType, IdentityProviderTypeType.SAML.Value, StringComparison.OrdinalIgnoreCase))
        {
            if (saml is null || oidc is not null)
            {
                throw new InvalidIdentityProviderException("SAML providers need a 'saml' block and no 'oidc' block.");
            }

            var hasUrl = !string.IsNullOrWhiteSpace(saml.MetadataUrl);
            var hasFile = !string.IsNullOrWhiteSpace(saml.MetadataFile);
            var hasManual = saml.Manual is not null;
            if ((hasUrl ? 1 : 0) + (hasFile ? 1 : 0) + (hasManual ? 1 : 0) != 1)
            {
                throw new InvalidIdentityProviderException(
                    "Exactly one of saml.metadataUrl, saml.metadataFile or saml.manual is required.");
            }

            var details = hasUrl
                ? new Dictionary<string, string> { ["MetadataURL"] = saml.MetadataUrl! }
                : new Dictionary<string, string>
                {
                    ["MetadataFile"] = hasFile
                        ? saml.MetadataFile!
                        : SamlMetadataBuilder.Build(saml.Manual!, timeProvider.GetUtcNow()),
                };

            if (saml.IdpSignout is { } idpSignout)
            {
                details["IDPSignout"] = idpSignout ? "true" : "false";
            }

            // A new IdP defaults to encrypted assertions and signed requests; an update keeps what's set.
            var encrypt = saml.EncryptedResponses ?? (current is null || IsTrue(current, EncryptedResponsesKey));
            var sign = saml.SignRequests ?? (current is null || current.ContainsKey(RequestSigningAlgorithmKey));
            details[EncryptedResponsesKey] = encrypt ? "true" : "false";
            if (sign)
            {
                details[RequestSigningAlgorithmKey] = RequestSigningAlgorithm;
            }

            var source = hasUrl ? SamlMetadataSources.MetadataUrl
                : hasFile ? SamlMetadataSources.MetadataFile
                : SamlMetadataSources.Manual;
            return (IdentityProviderTypeType.SAML, details, source);
        }

        if (string.Equals(providerType, IdentityProviderTypeType.OIDC.Value, StringComparison.OrdinalIgnoreCase))
        {
            if (oidc is null || saml is not null)
            {
                throw new InvalidIdentityProviderException("OIDC providers need an 'oidc' block and no 'saml' block.");
            }

            var clientSecret = string.IsNullOrWhiteSpace(oidc.ClientSecret)
                ? current?.GetValueOrDefault(OidcClientSecretKey)
                : oidc.ClientSecret;

            if (string.IsNullOrWhiteSpace(oidc.ClientId) || string.IsNullOrWhiteSpace(clientSecret) ||
                string.IsNullOrWhiteSpace(oidc.Issuer) || string.IsNullOrWhiteSpace(oidc.AuthorizeScopes))
            {
                throw new InvalidIdentityProviderException(
                    "oidc.clientId, oidc.clientSecret, oidc.issuer and oidc.authorizeScopes are required.");
            }

            return (IdentityProviderTypeType.OIDC, new Dictionary<string, string>
            {
                ["client_id"] = oidc.ClientId,
                [OidcClientSecretKey] = clientSecret,
                ["oidc_issuer"] = oidc.Issuer,
                ["authorize_scopes"] = oidc.AuthorizeScopes,
                ["attributes_request_method"] = string.IsNullOrWhiteSpace(oidc.AttributesRequestMethod)
                    ? "GET"
                    : oidc.AttributesRequestMethod,
            }, null);
        }

        throw new InvalidIdentityProviderException("ProviderType must be 'SAML' or 'OIDC'.");
    }

    private static bool IsTrue(Dictionary<string, string> details, string key) =>
        bool.TryParse(details.GetValueOrDefault(key), out var value) && value;

    private TenantIdentityProviderResponse ToResponse(IdentityProviderType provider, TenantIdentityProvider? record, Guid tenantId)
    {
        var details = provider.ProviderDetails ?? new Dictionary<string, string>();

        SamlProviderSettingsResponse? saml = null;
        if (provider.ProviderType == IdentityProviderTypeType.SAML)
        {
            var metadata = ReadSamlMetadata(details.GetValueOrDefault("MetadataFile"), provider.ProviderName, tenantId);
            saml = new SamlProviderSettingsResponse(
                details.GetValueOrDefault("MetadataURL"),
                bool.TryParse(details.GetValueOrDefault("IDPSignout"), out var idpSignout) ? idpSignout : null,
                metadata?.EntityId,
                metadata?.SsoUrl,
                metadata?.SingleLogoutUrl,
                metadata?.SigningCertificates ?? [],
                record?.SamlMetadataSource,
                IsTrue(details, EncryptedResponsesKey),
                details.ContainsKey(RequestSigningAlgorithmKey),
                ReadEncryptionCertificate(details.GetValueOrDefault(ActiveEncryptionCertificateKey), provider.ProviderName, tenantId));
        }

        var oidc = provider.ProviderType == IdentityProviderTypeType.OIDC
            ? new OidcProviderSettingsResponse(
                details.GetValueOrDefault("client_id"),
                details.GetValueOrDefault("oidc_issuer"),
                details.GetValueOrDefault("authorize_scopes"),
                details.GetValueOrDefault("attributes_request_method"))
            : null;

        return new TenantIdentityProviderResponse(
            provider.ProviderName,
            provider.ProviderType?.Value ?? string.Empty,
            provider.CreationDate,
            provider.LastModifiedDate,
            saml,
            oidc,
            provider.AttributeMapping ?? new Dictionary<string, string>(),
            provider.IdpIdentifiers ?? [],
            record?.Enabled ?? false,
            record?.Vendor);
    }

    /// <summary>
    /// Summarises the IdP metadata Cognito holds. Unreadable metadata only costs the edit form its
    /// pre-filled values, so it's logged rather than failing the request.
    /// </summary>
    private SamlMetadataReader.SamlMetadataSummary? ReadSamlMetadata(string? metadataXml, string providerName, Guid tenantId)
    {
        if (string.IsNullOrWhiteSpace(metadataXml))
        {
            return null;
        }

        try
        {
            return SamlMetadataReader.Read(metadataXml);
        }
        catch (Exception ex) when (ex is XmlException or FormatException)
        {
            logger.LogWarning(ex, "Couldn't read SAML metadata of identity provider {ProviderName} for tenant {TenantId}",
                providerName, tenantId);
            return null;
        }
    }

    /// <summary>Reads the encryption certificate Cognito generated for the IdP, logged and skipped if unreadable.</summary>
    private SamlCertificateResponse? ReadEncryptionCertificate(string? certificate, string providerName, Guid tenantId)
    {
        if (string.IsNullOrWhiteSpace(certificate))
        {
            return null;
        }

        try
        {
            using var parsed = SamlCertificateParser.Parse(certificate);
            return SamlCertificateParser.ToResponse(parsed);
        }
        catch (FormatException ex)
        {
            logger.LogWarning(ex, "Couldn't read the encryption certificate of identity provider {ProviderName} for tenant {TenantId}",
                providerName, tenantId);
            return null;
        }
    }
}
