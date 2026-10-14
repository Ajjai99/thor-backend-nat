using Microsoft.EntityFrameworkCore;
using Npgsql;
using Testcontainers.PostgreSql;
using Thor.DataConnectionManager;
using Thor.DataLayer.Data;
using Thor.DataLayer.Models.Tenants;
using Xunit;

namespace Thor.Workflows.Pai.Tests.Fixtures;

/// <summary>
/// A real Postgres with the tenant schema on it — every PAI read and write is raw SQL only Npgsql can
/// run. One container per test class; <see cref="ResetAsync"/> gives each test an empty tenant, so
/// coverage and definition hashes never leak between tests.
/// </summary>
public sealed class TenantDatabaseFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder().WithImage("postgres:16-alpine").Build();

    public Guid SourceId { get; private set; }

    public Guid ScanConfigId { get; private set; }

    public async Task InitializeAsync()
    {
        await _container.StartAsync();

        // EnsureCreated, not Migrate: tenant migrations are regenerated at publish, not tracked.
        await using var db = NewContext();
        await db.Database.EnsureCreatedAsync();
        await SeedBaseRowsAsync(db);
    }

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();

    public TenantDbContext NewContext() =>
        new(new DbContextOptionsBuilder<TenantDbContext>().UseNpgsql(_container.GetConnectionString()).Options);

    public ITenantConnectionManager ConnectionManager => new FakeTenantConnectionManager(_ => NewContext());

    /// <summary>Empties every table a PAI test touches and re-adds the rows an account needs before it can exist.</summary>
    public async Task ResetAsync()
    {
        await using var db = NewContext();
        await db.Database.ExecuteSqlRawAsync("""
            TRUNCATE tenant.pai_result, tenant.pai_seed, tenant.pai_group_path, tenant.pai_run_definition, tenant.pai_run_scan,
                     tenant.privilege_definition, tenant.workflow, tenant.ingest_change_event, tenant.edge,
                     tenant.scan_manifest, tenant.scan, tenant.scan_config, tenant.account, tenant.source,
                     tenant.account_type, tenant.authentication_methods CASCADE
            """);
        await SeedBaseRowsAsync(db);
    }

    private async Task SeedBaseRowsAsync(TenantDbContext db)
    {
        var authMethod = new AuthenticationMethod { Id = Guid.NewGuid(), TypeId = Guid.NewGuid(), Name = "test-auth" };
        var scanConfig = new ScanConfig
        {
            Id = Guid.NewGuid(), Name = "test-scan-config", AuthMethodId = authMethod.Id,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow, CreatedBy = "test", UpdatedBy = "test",
        };
        var source = new Source
        {
            Id = Guid.NewGuid(), ConnectorType = 308, Name = "test-source", Config = "{}", IsActive = true,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        };

        db.AccountTypes.Add(new AccountType { Id = WellKnownAccountTypes.Unclassified, Name = "Unclassified", Description = "d", IsHuman = false });
        db.AuthenticationMethods.Add(authMethod);
        db.ScanConfigs.Add(scanConfig);
        db.Sources.Add(source);
        await db.SaveChangesAsync();

        SourceId = source.Id;
        ScanConfigId = scanConfig.Id;
    }

    /// <summary>Runs one statement with parameters — for the arrangement steps EF would make verbose.</summary>
    public async Task ExecAsync(string sql, params NpgsqlParameter[] parameters)
    {
        await using var db = NewContext();
        await db.Database.ExecuteSqlRawAsync(sql, parameters);
    }
}

/// <summary>Hands every step a fresh context on the test database, skipping Master-DB routing.</summary>
public sealed class FakeTenantConnectionManager(Func<Guid, TenantDbContext> factory) : ITenantConnectionManager
{
    public Task<NpgsqlConnection> GetValidatedConnectionAsync(Guid tenantId, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<TenantDbContext> GetTenantDbContextAsync(Guid tenantId, CancellationToken cancellationToken = default) =>
        Task.FromResult(factory(tenantId));
}
