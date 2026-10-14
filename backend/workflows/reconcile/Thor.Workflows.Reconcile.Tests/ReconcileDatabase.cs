using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Testcontainers.PostgreSql;
using Thor.DataConnectionManager;
using Thor.DataLayer.Data;
using Thor.DataLayer.Models.Tenants;
using Thor.Workflows.Reconcile.Steps;
using Xunit;

namespace Thor.Workflows.Reconcile.Tests;

/// <summary>
/// A real tenant database (Testcontainers Postgres) holding one scan whose ingestion is complete, of one
/// source whose task completed. Entities are seeded the way promotion leaves them — the canonical row,
/// and a ledger row when this scan saw it — so whatever is seeded with <c>seen: false</c> is absent.
/// </summary>
public abstract class ReconcileDatabase : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder().WithImage("postgres:16-alpine").Build();

    protected TenantDbContext Context { get; private set; } = null!;

    protected Guid TenantId { get; } = Guid.NewGuid();

    protected Guid ScanConfigId { get; private set; }

    protected Guid ScanId { get; private set; }

    protected Guid SourceId { get; private set; }

    protected ReconcileRequest Request => new(TenantId, ScanId);

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
        await Context.SaveChangesAsync();
        ScanConfigId = scanConfig.Id;

        SourceId = await AddSourceAsync("ad-domain");
        ScanId = await AddScanAsync(ingestionCompleted: true);
        await AddTaskAsync(ScanId, SourceId, ScanTaskStatus.Completed);
    }

    public async Task DisposeAsync()
    {
        await Context.DisposeAsync();
        await _container.DisposeAsync();
    }

    protected async Task<Guid> AddSourceAsync(string name, short? thresholdPercent = null)
    {
        var source = new Source
        {
            Id = Guid.NewGuid(), ConnectorType = 1, Name = name, Config = "{}", IsActive = true,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        };
        if (thresholdPercent is { } threshold)
        {
            source.AutoDeleteThresholdPercent = threshold;
        }

        Context.Sources.Add(source);
        Context.ScanSourceMappings.Add(new ScanSourceMapping { Id = Guid.NewGuid(), ScanConfigId = ScanConfigId, SourceId = source.Id });
        await Context.SaveChangesAsync();
        return source.Id;
    }

    protected async Task<Guid> AddScanAsync(bool ingestionCompleted)
    {
        var scan = new Scan
        {
            Id = Guid.NewGuid(), ScanConfigId = ScanConfigId, ScanType = "full", Status = "running",
            IngestionCompletedAt = ingestionCompleted ? DateTimeOffset.UtcNow : null,
        };
        Context.Scans.Add(scan);
        await Context.SaveChangesAsync();
        return scan.Id;
    }

    protected async Task AddTaskAsync(Guid scanId, Guid sourceId, string status, DateTimeOffset? createdAt = null)
    {
        Context.Tasks.Add(new ScanTask
        {
            Id = Guid.NewGuid(), ScanId = scanId, SourceId = sourceId, Status = status, CreatedAt = createdAt ?? DateTimeOffset.UtcNow,
        });
        await Context.SaveChangesAsync();
    }

    protected Task SetThresholdAsync(Guid sourceId, short percent) =>
        Context.Database.ExecuteSqlRawAsync("UPDATE tenant.source SET auto_delete_threshold_percent = {0} WHERE id = {1}", percent, sourceId);

    protected async Task<Guid> AccountAsync(string nativeId, bool seen = true, Guid? sourceId = null)
    {
        var id = Guid.NewGuid();
        Context.Accounts.Add(new Account
        {
            Id = id, SourceId = sourceId ?? SourceId, ConnectorType = 1, NativeId = nativeId,
            AccountKind = "user", IsHuman = true, DisplayName = nativeId, SamAccountName = nativeId,
            Upn = $"{nativeId}@test.com", Email = $"{nativeId}@test.com", DomainName = "test.com", FilerName = "",
            NativeAccountId = "S-1-5-1", AccountTypeId = WellKnownAccountTypes.Unclassified,
            RawAttributes = """{"AliasKeys":["k"],"EdgeRefs":[{"Rel":"MEMBER_OF"}]}""",
            ContentHash = "hash-" + nativeId, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        });
        await SeenAsync(EntityTypes.Account, nativeId, seen, sourceId);
        return id;
    }

    /// <summary>Accounts <c>{prefix}-0</c>…, all seen by this scan.</summary>
    protected async Task SeedSeenAccountsAsync(string prefix, int count)
    {
        for (var i = 0; i < count; i++)
        {
            await AccountAsync($"{prefix}-{i}");
        }
    }

    protected async Task<Guid> GroupAsync(string nativeId, bool seen = true)
    {
        var id = Guid.NewGuid();
        Context.Grps.Add(new Grp
        {
            Id = id, SourceId = SourceId, ConnectorType = 1, NativeId = nativeId, GroupClass = "security_global",
            DisplayName = nativeId, RawAttributes = """{"AliasKeys":[],"EdgeRefs":[]}""", ContentHash = "hash-" + nativeId,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        });
        await SeenAsync(EntityTypes.Group, nativeId, seen);
        return id;
    }

    protected async Task<Guid> AssetAsync(string nativeId, bool seen = true)
    {
        var id = Guid.NewGuid();
        Context.Assets.Add(new Asset
        {
            Id = id, SourceId = SourceId, ConnectorType = 7, NativeId = nativeId, AssetType = "safe",
            DisplayName = nativeId, FullPath = $"/{nativeId}", FilerName = "",
            RawAttributes = """{"AliasKeys":[],"EdgeRefs":[]}""", ContentHash = "hash-" + nativeId,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        });
        await SeenAsync(EntityTypes.Asset, nativeId, seen);
        return id;
    }

    protected async Task<Guid> EdgeAsync((string Type, Guid Id) from, (string Type, Guid Id) to)
    {
        var id = Guid.NewGuid();
        Context.Edges.Add(new Edge
        {
            Id = id, FromType = from.Type, FromId = from.Id, ToType = to.Type, ToId = to.Id, RelType = "MEMBER_OF",
            ContentHash = "hash", CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        });
        await Context.SaveChangesAsync();
        return id;
    }

    protected async Task AliasAndParkedRefAsync(string type, Guid id)
    {
        Context.EntityAliases.Add(new EntityAlias
        {
            Id = Guid.NewGuid(), EntityType = type, EntityId = id, SourceId = SourceId, ConnectorType = 1,
            AliasKey = "alias-" + id, CreatedAt = DateTimeOffset.UtcNow,
        });
        Context.OneSidedRefs.Add(new OneSidedRef
        {
            Id = Guid.NewGuid(), Key = "ref-" + id, OwnerId = id, OwnerType = type, Rel = "MEMBER_OF", Dir = "out",
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await Context.SaveChangesAsync();
    }

    /// <summary>An owner as Ownership leaves one: a rank-1 <c>party_assignment</c> on the entity, active unless said otherwise.</summary>
    protected async Task<Guid> OwnerAsync(string type, Guid entityId, bool isActive = true)
    {
        var identity = new IdentityRecord
        {
            Id = Guid.NewGuid(), Source = "hr_feed", HrEmployeeId = "E-" + entityId, DisplayName = "Owner Person",
            Email = "owner@test.com", GivenName = "Owner", Surname = "Person", IsActive = true,
            ContentHash = Guid.NewGuid().ToString(), CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        };
        var assignment = new PartyAssignment
        {
            Id = Guid.NewGuid(), EntityType = type, EntityId = entityId, IdentityId = identity.Id, Rank = 1, IsActive = isActive,
            VoteDistribution = "{}", ContributingRuleIds = [], PrecisionScoreSnapshot = "{}", RunId = Guid.NewGuid().ToString(),
            AssignedAt = DateTimeOffset.UtcNow,
        };
        Context.Identities.Add(identity);
        Context.PartyAssignments.Add(assignment);
        await Context.SaveChangesAsync();
        return assignment.Id;
    }

    protected async Task SeenAsync(string type, string nativeId, bool seen = true, Guid? sourceId = null, Guid? scanId = null)
    {
        if (seen)
        {
            Context.ScanSeenEntities.Add(new ScanSeenEntity
            {
                ScanId = scanId ?? ScanId, SourceId = sourceId ?? SourceId, EntityType = type, NativeId = nativeId,
            });
        }

        await Context.SaveChangesAsync();
    }

    /// <summary>Drives a whole run as the state machine does: start-run, every wave's chunks, then finalize. Small chunks, so several waves run.</summary>
    protected async Task<ReconcileWave> RunAsync(int rowsPerChunk = 2, int chunksPerWave = 2)
    {
        var logs = NullLoggerFactory.Instance;
        var connections = new FakeTenantConnectionManager(Context);

        var first = await new ReconcileStartRunStep(logs, connections, rowsPerChunk, chunksPerWave).RunAsync(Context, Request);
        var wave = first;
        while (wave.HasMore)
        {
            foreach (var chunk in wave.Chunks)
            {
                await new ReconcileDeleteChunkStep(logs, connections).RunAsync(Context, chunk);
            }

            wave = ReconcileWavePlanner.WaveAt(Request, wave.Total, wave.WaveIndex + 1, rowsPerChunk, chunksPerWave);
        }

        await new ReconcileFinalizeStep(logs, connections).RunAsync(Context, Request);
        return first;
    }

    /// <summary>A second, independent connection to the same database — what a concurrent Map item has.</summary>
    protected TenantDbContext NewContext() =>
        new(new DbContextOptionsBuilder<TenantDbContext>().UseNpgsql(_container.GetConnectionString()).Options);

    protected Task<Account> AccountRowAsync(Guid id) => Context.Accounts.AsNoTracking().SingleAsync(a => a.Id == id);

    protected Task<List<ReconcileDecision>> DecisionsAsync(Guid? sourceId = null) =>
        Context.ReconcileDecisions.AsNoTracking().Where(d => d.SourceId == (sourceId ?? SourceId)).ToListAsync();

    protected Task<WorkflowEntity> WorkflowRowAsync() =>
        Context.Workflows.AsNoTracking().SingleAsync(w => w.WorkflowType == Thor.Workflows.Abstractions.WorkflowTypes.Reconcile);

    private sealed class FakeTenantConnectionManager(TenantDbContext db) : ITenantConnectionManager
    {
        public Task<Npgsql.NpgsqlConnection> GetValidatedConnectionAsync(Guid tenantId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<TenantDbContext> GetTenantDbContextAsync(Guid tenantId, CancellationToken cancellationToken = default) =>
            Task.FromResult(db);
    }
}
