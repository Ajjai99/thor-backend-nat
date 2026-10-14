using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;
using Thor.DataLayer.Data;
using Thor.DataLayer.Models.Tenants;
using Xunit;

namespace Thor.Workflows.Ingestion.Tests.Steps;

/// <summary>
/// A real tenant database (Testcontainers Postgres) seeded with one scan, for <see cref="ResolveEdgesStepTests"/>.
/// Rows are seeded the way promotion leaves them: the entity, its alias, and an
/// <c>ingest_change_event</c> naming the manifest that changed it.
/// </summary>
public abstract class ResolveEdgesDatabase : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder().WithImage("postgres:16-alpine").Build();

    protected TenantDbContext Context { get; private set; } = null!;

    protected Guid SourceId { get; private set; }

    protected Guid ScanId { get; private set; }

    protected Guid TenantId { get; } = Guid.NewGuid();

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        Context = new TenantDbContext(new DbContextOptionsBuilder<TenantDbContext>().UseNpgsql(_container.GetConnectionString()).Options);
        await Context.Database.EnsureCreatedAsync();

        Context.AccountTypes.Add(new AccountType
        {
            Id = WellKnownAccountTypes.Unclassified, Name = "Unclassified",
            Description = "Default account type for newly-promoted accounts pending classification.", IsHuman = false,
        });

        var authMethod = new AuthenticationMethod { Id = Guid.NewGuid(), TypeId = Guid.NewGuid(), Name = "test-auth" };
        Context.AuthenticationMethods.Add(authMethod);
        var scanConfig = new ScanConfig
        {
            Id = Guid.NewGuid(), Name = "test-scan-config", AuthMethodId = authMethod.Id,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow, CreatedBy = "test", UpdatedBy = "test",
        };
        Context.ScanConfigs.Add(scanConfig);
        var scan = new Scan { Id = Guid.NewGuid(), ScanConfigId = scanConfig.Id, ScanType = "initial", Status = "running" };
        Context.Scans.Add(scan);
        var source = new Source
        {
            Id = Guid.NewGuid(), ConnectorType = 1, Name = "test-source",
            Config = "{}", IsActive = true, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        };
        Context.Sources.Add(source);
        await Context.SaveChangesAsync();

        (SourceId, ScanId) = (source.Id, scan.Id);
    }

    public async Task DisposeAsync()
    {
        await Context.DisposeAsync();
        await _container.DisposeAsync();
    }

    protected async Task<Guid> SeedManifestAsync()
    {
        var manifest = new ScanManifest
        {
            Id = Guid.NewGuid(), ScanId = ScanId, FileLocations = [], Status = "pending",
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        };
        Context.ScanManifests.Add(manifest);
        await Context.SaveChangesAsync();
        return manifest.Id;
    }

    protected async Task<Guid> InsertAccountAsync(
        Guid scanManifestId, string nativeId, bool isDeleted = false,
        string? aliasKey = null, params (string Rel, string Dir, string Key, string? TargetType)[] edgeRefs)
    {
        var id = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        Context.Accounts.Add(new Account
        {
            Id = id, SourceId = SourceId, ConnectorType = 1, NativeId = nativeId,
            AccountKind = "user", IsHuman = true, DisplayName = nativeId, SamAccountName = nativeId,
            Upn = $"{nativeId}@test.com", Email = $"{nativeId}@test.com", DomainName = "test.com", FilerName = "",
            NativeAccountId = "S-1-5-1", IsDeleted = isDeleted, IsDisabled = false,
            AccountTypeId = WellKnownAccountTypes.Unclassified,
            RawAttributes = RawAttributesJson(aliasKey, edgeRefs),
            ContentHash = "hash-" + nativeId, HashVersion = 0, CreatedAt = now, UpdatedAt = now,
        });
        AddAliasAndChange(scanManifestId, "account", id, aliasKey, isDeleted ? "deleted" : "inserted");
        await Context.SaveChangesAsync();
        return id;
    }

    protected async Task<Guid> InsertGroupAsync(
        Guid scanManifestId, string nativeId, bool isDeleted = false,
        string? aliasKey = null, params (string Rel, string Dir, string Key, string? TargetType)[] edgeRefs)
    {
        var id = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        Context.Grps.Add(new Grp
        {
            Id = id, SourceId = SourceId, ConnectorType = 1, NativeId = nativeId,
            GroupClass = "security_global", DisplayName = nativeId, Email = null, DomainName = "test.com",
            IsLargeGroup = false, IsDeleted = isDeleted,
            RawAttributes = RawAttributesJson(aliasKey, edgeRefs),
            ContentHash = "hash-" + nativeId, HashVersion = 0, CreatedAt = now, UpdatedAt = now,
        });
        AddAliasAndChange(scanManifestId, "grp", id, aliasKey, isDeleted ? "deleted" : "inserted");
        await Context.SaveChangesAsync();
        return id;
    }

    protected async Task<Guid> InsertAssetAsync(Guid scanManifestId, string nativeId)
    {
        var id = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        Context.Assets.Add(new Asset
        {
            Id = id, SourceId = SourceId, ConnectorType = 7, NativeId = nativeId,
            AssetType = "safe", DisplayName = nativeId, FullPath = $"/{nativeId}", FilerName = "",
            FileSize = 0, FileCount = 0, BrokenAcl = false, IsProtected = true, ParentAssetId = null,
            RawAttributes = """{"AliasKeys":[],"EdgeRefs":[]}""", ContentHash = "hash-" + nativeId,
            HashVersion = 0, CreatedAt = now, UpdatedAt = now,
        });
        AddAliasAndChange(scanManifestId, "asset", id, aliasKey: null, "inserted");
        await Context.SaveChangesAsync();
        return id;
    }

    private void AddAliasAndChange(Guid scanManifestId, string entityType, Guid id, string? aliasKey, string changeType)
    {
        var now = DateTimeOffset.UtcNow;
        if (aliasKey is not null)
        {
            Context.EntityAliases.Add(new EntityAlias
            {
                Id = Guid.NewGuid(), EntityType = entityType, EntityId = id, SourceId = SourceId,
                ConnectorType = 1, AliasKey = aliasKey, CreatedAt = now,
            });
        }
        Context.IngestChangeEvents.Add(new IngestChangeEvent
        {
            Id = Guid.NewGuid(), ScanManifestId = scanManifestId, ScanId = ScanId, EntityType = entityType,
            EntityId = id, ChangeType = changeType, OccurredAt = now,
        });
    }

    private static string RawAttributesJson(string? aliasKey, (string Rel, string Dir, string Key, string? TargetType)[] edgeRefs)
    {
        if (aliasKey is null)
        {
            return """{"AliasKeys":[],"EdgeRefs":[]}""";
        }

        var refsJson = string.Join(",", edgeRefs.Select(r =>
            $$"""{"Rel":"{{r.Rel}}","Dir":"{{r.Dir}}","Key":"{{r.Key}}","TargetType":{{(r.TargetType is null ? "null" : $"\"{r.TargetType}\"")}}}"""));
        return $$"""{"AliasKeys":["{{aliasKey}}"],"EdgeRefs":[{{refsJson}}]}""";
    }
}
