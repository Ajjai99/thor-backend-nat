using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;
using Thor.DataLayer.Data;
using Thor.DataLayer.Models.Tenants;
using Thor.Workflows.Abstractions;
using Xunit;

namespace Thor.Workflows.GraphLoad.Tests;

/// <summary>
/// A real tenant database (Testcontainers Postgres) seeded with one scan. Rows are seeded the way
/// ingestion leaves them: the entity or edge, and an <c>ingest_change_event</c> naming the manifest
/// that changed it. A manifest counts only once <see cref="MarkIngestionAsync"/> says it completed.
/// </summary>
public abstract class GraphLoadDatabase : IAsyncLifetime
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

    /// <summary>A manifest of this scan, its ingestion row already in <paramref name="ingestionStatus"/> (null for none).</summary>
    protected async Task<Guid> SeedManifestAsync(string? ingestionStatus = "completed")
    {
        var manifest = new ScanManifest
        {
            Id = Guid.NewGuid(), ScanId = ScanId, FileLocations = [], Status = "pending",
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        };
        Context.ScanManifests.Add(manifest);
        if (ingestionStatus is not null)
        {
            Context.Workflows.Add(new WorkflowEntity
            {
                Id = Guid.NewGuid(), ScanId = ScanId, ScanManifestId = manifest.Id, WorkflowType = WorkflowTypes.Ingestion,
                Trigger = WorkflowTriggers.StepFunctions, Status = ingestionStatus, StartedAt = DateTimeOffset.UtcNow, CompletedAt = DateTimeOffset.UtcNow,
            });
        }
        await Context.SaveChangesAsync();
        return manifest.Id;
    }

    /// <summary>A manifest of another scan, for rows that predate this one.</summary>
    protected async Task<Guid> SeedEarlierScanManifestAsync()
    {
        var scan = await Context.Scans.AsNoTracking().SingleAsync(s => s.Id == ScanId);
        var earlier = new Scan { Id = Guid.NewGuid(), ScanConfigId = scan.ScanConfigId, ScanType = "initial", Status = "completed" };
        Context.Scans.Add(earlier);
        var manifest = new ScanManifest
        {
            Id = Guid.NewGuid(), ScanId = earlier.Id, FileLocations = [], Status = "completed",
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        };
        Context.ScanManifests.Add(manifest);
        await Context.SaveChangesAsync();
        return manifest.Id;
    }

    protected async Task<Guid> InsertAccountAsync(Guid scanManifestId, string nativeId, bool isDeleted = false)
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
            RawAttributes = """{"AliasKeys":[],"EdgeRefs":[]}""",
            ContentHash = "hash-" + nativeId, HashVersion = 0, CreatedAt = now, UpdatedAt = now,
        });
        await AddChangeAsync(scanManifestId, GraphLoadKinds.Account, id, isDeleted ? "deleted" : "inserted");
        return id;
    }

    protected async Task<Guid> InsertGroupAsync(Guid scanManifestId, string nativeId, bool isDeleted = false)
    {
        var id = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        Context.Grps.Add(new Grp
        {
            Id = id, SourceId = SourceId, ConnectorType = 1, NativeId = nativeId,
            GroupClass = "security_global", DisplayName = nativeId, Email = null, DomainName = "test.com",
            IsLargeGroup = false, IsDeleted = isDeleted,
            RawAttributes = """{"AliasKeys":[],"EdgeRefs":[]}""",
            ContentHash = "hash-" + nativeId, HashVersion = 0, CreatedAt = now, UpdatedAt = now,
        });
        await AddChangeAsync(scanManifestId, GraphLoadKinds.Group, id, isDeleted ? "deleted" : "inserted");
        return id;
    }

    protected async Task<Guid> InsertAssetAsync(Guid scanManifestId, string nativeId, bool isDeleted = false)
    {
        var id = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        Context.Assets.Add(new Asset
        {
            Id = id, SourceId = SourceId, ConnectorType = 7, NativeId = nativeId,
            AssetType = "safe", DisplayName = nativeId, FullPath = $"/{nativeId}", FilerName = "",
            FileSize = 0, FileCount = 0, BrokenAcl = false, IsProtected = true, ParentAssetId = null,
            IsDeleted = isDeleted,
            RawAttributes = """{"AliasKeys":[],"EdgeRefs":[]}""", ContentHash = "hash-" + nativeId,
            HashVersion = 0, CreatedAt = now, UpdatedAt = now,
        });
        await AddChangeAsync(scanManifestId, GraphLoadKinds.Asset, id, isDeleted ? "deleted" : "inserted");
        return id;
    }

    /// <summary>
    /// Marks a seeded row deleted the way the reconcile workflow does: soft-deleted, with a
    /// <c>deleted</c> event of this scan that belongs to no manifest.
    /// </summary>
    protected async Task ReconcileDeleteAsync(string entityType, Guid id)
    {
        _ = entityType switch
        {
            GraphLoadKinds.Account => await Context.Accounts.Where(a => a.Id == id).ExecuteUpdateAsync(s => s.SetProperty(a => a.IsDeleted, true)),
            GraphLoadKinds.Group => await Context.Grps.Where(g => g.Id == id).ExecuteUpdateAsync(s => s.SetProperty(g => g.IsDeleted, true)),
            GraphLoadKinds.Asset => await Context.Assets.Where(a => a.Id == id).ExecuteUpdateAsync(s => s.SetProperty(a => a.IsDeleted, true)),
            GraphLoadKinds.Edge => await Context.Edges.Where(e => e.Id == id).ExecuteUpdateAsync(s => s.SetProperty(e => e.IsDeleted, true)),
            _ => throw new ArgumentOutOfRangeException(nameof(entityType), entityType, null),
        };
        Context.IngestChangeEvents.Add(new IngestChangeEvent
        {
            Id = Guid.NewGuid(), ScanManifestId = null, ScanId = ScanId, EntityType = entityType,
            EntityId = id, ChangeType = "deleted", OccurredAt = DateTimeOffset.UtcNow,
        });
        await Context.SaveChangesAsync();
    }

    protected async Task<Guid> InsertEdgeAsync(
        Guid scanManifestId, (string Type, Guid Id) from, (string Type, Guid Id) to, string relType = "MEMBER_OF", bool isDeleted = false)
    {
        var id = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        Context.Edges.Add(new Edge
        {
            Id = id, FromType = from.Type, FromId = from.Id, ToType = to.Type, ToId = to.Id, RelType = relType,
            Props = null, IsDeleted = isDeleted, ContentHash = "hash", CreatedAt = now, UpdatedAt = now,
        });
        await AddChangeAsync(scanManifestId, GraphLoadKinds.Edge, id, isDeleted ? "deleted" : "inserted");
        return id;
    }

    private async Task AddChangeAsync(Guid scanManifestId, string entityType, Guid id, string changeType)
    {
        var scanId = (await Context.ScanManifests.AsNoTracking().SingleAsync(m => m.Id == scanManifestId)).ScanId;
        Context.IngestChangeEvents.Add(new IngestChangeEvent
        {
            Id = Guid.NewGuid(), ScanManifestId = scanManifestId, ScanId = scanId, EntityType = entityType,
            EntityId = id, ChangeType = changeType, OccurredAt = DateTimeOffset.UtcNow,
        });
        await Context.SaveChangesAsync();
    }
}
