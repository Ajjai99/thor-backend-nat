using Microsoft.EntityFrameworkCore;
using Npgsql;
using Testcontainers.PostgreSql;
using Thor.DataConnectionManager;
using Thor.DataLayer.Data;
using Thor.DataLayer.Models.Tenants;
using Thor.DataLayer.Repositories;
using Thor.ScanGate.Core;
using Thor.Workflows.Abstractions;

namespace Thor.ScanGate.Test;

/// <summary>
/// Integration tests against a real Postgres (via Testcontainers) for <see cref="TenantScanGateStore"/>
/// and the <see cref="ScanFileRepository"/> writes the gate relies on — the upserts' conflict clauses
/// are the behaviour under test, so there is nothing worth faking. Requires Docker running locally.
/// </summary>
public sealed class TenantScanGateStoreTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder()
        .WithImage("postgres:16-alpine")
        .Build();

    private DbContextOptions<TenantDbContext> _options = null!;
    private TenantDbContext _context = null!;
    private TenantScanGateStore _store = null!;
    private readonly Guid _tenantId = Guid.NewGuid();

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        _options = new DbContextOptionsBuilder<TenantDbContext>().UseNpgsql(_container.GetConnectionString()).Options;
        _context = new TenantDbContext(_options);
        await _context.Database.EnsureCreatedAsync();

        // The store disposes the context it is handed on every call, so each call gets its own.
        _store = new TenantScanGateStore(new FakeTenantConnectionManager(_ => new TenantDbContext(_options)));
    }

    public async Task DisposeAsync()
    {
        await _context.DisposeAsync();
        await _container.DisposeAsync();
    }

    private sealed class FakeTenantConnectionManager(Func<Guid, TenantDbContext> factory) : ITenantConnectionManager
    {
        public Task<NpgsqlConnection> GetValidatedConnectionAsync(Guid tenantId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<TenantDbContext> GetTenantDbContextAsync(Guid tenantId, CancellationToken cancellationToken = default) =>
            Task.FromResult(factory(tenantId));
    }

    private async Task<(Guid ScanId, Guid TaskId)> SeedScanWithTaskAsync()
    {
        var authMethod = new AuthenticationMethod { Id = Guid.NewGuid(), TypeId = Guid.NewGuid(), Name = "test-auth" };
        var scanConfig = new ScanConfig
        {
            Id = Guid.NewGuid(), Name = "test-scan-config-" + Guid.NewGuid(), AuthMethodId = authMethod.Id,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow, CreatedBy = "test", UpdatedBy = "test",
        };
        var source = new Source
        {
            Id = Guid.NewGuid(), ConnectorType = 308, Name = "test-source", Config = "{}", IsActive = true,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        };
        var scan = new Scan { Id = Guid.NewGuid(), ScanConfigId = scanConfig.Id, ScanType = "initial", Status = ScanStatus.InProgress };
        var task = new ScanTask { Id = Guid.NewGuid(), ScanId = scan.Id, SourceId = source.Id, Status = ScanTaskStatus.Completed };
        _context.AddRange(authMethod, scanConfig, source, scan, task);
        await _context.SaveChangesAsync();
        return (scan.Id, task.Id);
    }

    private async Task<Guid> SeedManifestAsync(Guid scanId)
    {
        var manifest = new ScanManifest
        {
            Id = Guid.NewGuid(), ScanId = scanId, FileLocations = [], Status = "processing",
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        };
        _context.ScanManifests.Add(manifest);
        await _context.SaveChangesAsync();
        return manifest.Id;
    }

    private async Task SeedWorkflowAsync(Guid? scanId, Guid manifestId, string type, string status, bool terminal)
    {
        _context.Workflows.Add(new WorkflowEntity
        {
            Id = Guid.NewGuid(), ScanId = scanId, ScanManifestId = manifestId, WorkflowType = type, Trigger = "step_functions",
            Status = status, StartedAt = DateTimeOffset.UtcNow, CompletedAt = terminal ? DateTimeOffset.UtcNow : null,
        });
        await _context.SaveChangesAsync();
    }

    private static ScanFile File(Guid scanId, Guid taskId, string location, string status, DateTimeOffset? expiresAt = null) => new()
    {
        Id = Guid.NewGuid(), ScanId = scanId, ScanTaskId = taskId, FileLocation = location, Status = status,
        UploadExpiresAt = expiresAt, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
    };

    private async Task<ScanFile> ReadFileAsync(string location) =>
        await new TenantDbContext(_options).ScanFiles.AsNoTracking().SingleAsync(f => f.FileLocation == location);

    [Fact]
    public async Task RegisterPending_ThenMarkReceived_UpgradesTheRowAndKeepsItsExpiry()
    {
        var (scanId, taskId) = await SeedScanWithTaskAsync();
        var expiresAt = new DateTimeOffset(2026, 9, 25, 12, 15, 0, TimeSpan.Zero);
        var repository = new ScanFileRepository(_context);

        await repository.RegisterPendingAsync(File(scanId, taskId, "k1", ScanFileStatus.Pending, expiresAt));
        await repository.MarkReceivedAsync([File(scanId, taskId, "k1", ScanFileStatus.Received)]);

        var row = await ReadFileAsync("k1");
        Assert.Equal(ScanFileStatus.Received, row.Status);
        Assert.Equal(expiresAt, row.UploadExpiresAt);
    }

    /// <summary>
    /// Thor.CreateManifest and ingestion's list step both mark received, in either order and more
    /// than once. Neither may undo an outcome a Map item has already written.
    /// </summary>
    [Fact]
    public async Task MarkReceived_NeverDowngradesAnIngestedOrFailedFile()
    {
        var (scanId, taskId) = await SeedScanWithTaskAsync();
        var repository = new ScanFileRepository(_context);
        await repository.MarkStatusAsync(File(scanId, taskId, "ingested", ScanFileStatus.Ingested));
        await repository.MarkStatusAsync(File(scanId, taskId, "failed", ScanFileStatus.Failed));

        await repository.MarkReceivedAsync(
        [
            File(scanId, taskId, "ingested", ScanFileStatus.Received),
            File(scanId, taskId, "failed", ScanFileStatus.Received),
            File(scanId, taskId, "new", ScanFileStatus.Received),
        ]);

        Assert.Equal(ScanFileStatus.Ingested, (await ReadFileAsync("ingested")).Status);
        Assert.Equal(ScanFileStatus.Failed, (await ReadFileAsync("failed")).Status);
        Assert.Equal(ScanFileStatus.Received, (await ReadFileAsync("new")).Status);
    }

    [Fact]
    public async Task RegisterPending_IsANoOpForAFileAlreadyRecorded()
    {
        var (scanId, taskId) = await SeedScanWithTaskAsync();
        var repository = new ScanFileRepository(_context);
        await repository.MarkStatusAsync(File(scanId, taskId, "k1", ScanFileStatus.Ingested));

        await repository.RegisterPendingAsync(File(scanId, taskId, "k1", ScanFileStatus.Pending, DateTimeOffset.UtcNow));

        Assert.Equal(ScanFileStatus.Ingested, (await ReadFileAsync("k1")).Status);
    }

    /// <summary>An object can turn up between the gate's HeadObject and its write; that file must stay Received.</summary>
    [Fact]
    public async Task MarkAbandoned_OnlyTouchesFilesStillPending()
    {
        var (scanId, taskId) = await SeedScanWithTaskAsync();
        var repository = new ScanFileRepository(_context);
        var pending = File(scanId, taskId, "pending", ScanFileStatus.Pending, DateTimeOffset.UtcNow);
        await repository.RegisterPendingAsync(pending);
        var arrived = File(scanId, taskId, "arrived", ScanFileStatus.Pending, DateTimeOffset.UtcNow);
        await repository.RegisterPendingAsync(arrived);
        await repository.MarkReceivedAsync([File(scanId, taskId, "arrived", ScanFileStatus.Received)]);

        await _store.MarkAbandonedAsync(_tenantId, [pending.Id, arrived.Id]);

        Assert.Equal(ScanFileStatus.Abandoned, (await ReadFileAsync("pending")).Status);
        Assert.Equal(ScanFileStatus.Received, (await ReadFileAsync("arrived")).Status);
    }

    /// <summary>ATRE and Ownership rows carry only the manifest, so they must be found through it.</summary>
    [Fact]
    public async Task Load_FindsEveryManifestsWorkflows_IncludingRowsWithNoScanId_AndNothingFromOtherScans()
    {
        var (scanId, taskId) = await SeedScanWithTaskAsync();
        var (otherScanId, _) = await SeedScanWithTaskAsync();
        var manifestId = await SeedManifestAsync(scanId);
        var otherManifestId = await SeedManifestAsync(otherScanId);
        await SeedWorkflowAsync(scanId, manifestId, WorkflowTypes.Ingestion, "completed", terminal: true);
        await SeedWorkflowAsync(null, manifestId, WorkflowTypes.Atre, "completed", terminal: true);
        await SeedWorkflowAsync(null, manifestId, WorkflowTypes.Ownership, "started", terminal: false);
        await SeedWorkflowAsync(otherScanId, otherManifestId, WorkflowTypes.Ingestion, "started", terminal: false);
        var repository = new ScanFileRepository(_context);
        await repository.RegisterPendingAsync(File(scanId, taskId, "pending", ScanFileStatus.Pending, DateTimeOffset.UtcNow));
        await repository.MarkStatusAsync(File(scanId, taskId, "done", ScanFileStatus.Ingested));

        var snapshot = await _store.LoadAsync(_tenantId, scanId);

        Assert.Single(snapshot.Tasks);
        Assert.Equal([manifestId], snapshot.ManifestIds);
        Assert.Equal(
            [WorkflowTypes.Atre, WorkflowTypes.Ingestion, WorkflowTypes.Ownership],
            snapshot.ManifestWorkflows.Select(w => w.WorkflowType).Order());
        Assert.Equal("pending", Assert.Single(snapshot.PendingFiles).FileLocation);
        Assert.Equal(1, snapshot.FileCountsByStatus[ScanFileStatus.Pending]);
        Assert.Equal(1, snapshot.FileCountsByStatus[ScanFileStatus.Ingested]);
    }

    /// <summary>The reconcile and graph-load rows carry the scan and no manifest, so they are not among the manifests' workflows.</summary>
    [Fact]
    public async Task Load_FindsTheScansReconcileAndGraphLoadRows_AndNoOtherScansOrRowTypes()
    {
        var (scanId, _) = await SeedScanWithTaskAsync();
        var (otherScanId, _) = await SeedScanWithTaskAsync();
        var manifestId = await SeedManifestAsync(scanId);
        await SeedWorkflowAsync(scanId, manifestId, WorkflowTypes.Ingestion, "completed", terminal: true);
        await SeedScanWorkflowAsync(scanId, WorkflowTypes.Reconcile, "completed");
        await SeedScanWorkflowAsync(scanId, WorkflowTypes.GraphLoad, "started");
        await SeedScanWorkflowAsync(otherScanId, WorkflowTypes.Reconcile, "started");
        await SeedScanWorkflowAsync(otherScanId, WorkflowTypes.GraphLoad, "completed");

        var snapshot = await _store.LoadAsync(_tenantId, scanId);

        Assert.Equal("completed", Assert.Single(snapshot.Reconciles).Status);
        Assert.Equal("started", Assert.Single(snapshot.GraphLoads).Status);
        Assert.Equal(WorkflowTypes.Ingestion, Assert.Single(snapshot.ManifestWorkflows).WorkflowType);
    }

    private async Task SeedScanWorkflowAsync(Guid scanId, string type, string status)
    {
        _context.Workflows.Add(new WorkflowEntity
        {
            Id = Guid.NewGuid(), ScanId = scanId, ScanManifestId = null, WorkflowType = type, Trigger = "step_functions",
            Status = status, StartedAt = DateTimeOffset.UtcNow, RunId = Guid.NewGuid(),
        });
        await _context.SaveChangesAsync();
    }

    [Fact]
    public async Task StampIngestionCompleted_SetsOnlyThatTimestamp_Once()
    {
        var (scanId, _) = await SeedScanWithTaskAsync();
        var first = new DateTimeOffset(2026, 9, 25, 12, 0, 0, TimeSpan.Zero);

        await _store.StampIngestionCompletedAsync(_tenantId, scanId, first);
        await _store.StampIngestionCompletedAsync(_tenantId, scanId, first.AddHours(1));

        var scan = await new TenantDbContext(_options).Scans.AsNoTracking().SingleAsync(s => s.Id == scanId);
        Assert.Equal(first, scan.IngestionCompletedAt);
        Assert.Null(scan.AnalysisCompletedAt);
    }

    [Fact]
    public async Task FindScanIdForManifest_ResolvesThroughTheManifest()
    {
        var (scanId, _) = await SeedScanWithTaskAsync();
        var manifestId = await SeedManifestAsync(scanId);

        Assert.Equal(scanId, await _store.FindScanIdForManifestAsync(_tenantId, manifestId));
        Assert.Null(await _store.FindScanIdForManifestAsync(_tenantId, Guid.NewGuid()));
    }

    [Fact]
    public async Task StampCompleted_SetsBothTimestampsOnce()
    {
        var (scanId, _) = await SeedScanWithTaskAsync();
        var first = new DateTimeOffset(2026, 9, 25, 12, 0, 0, TimeSpan.Zero);

        await _store.StampCompletedAsync(_tenantId, scanId, first);
        await _store.StampCompletedAsync(_tenantId, scanId, first.AddHours(1));

        var scan = await new TenantDbContext(_options).Scans.AsNoTracking().SingleAsync(s => s.Id == scanId);
        Assert.Equal(first, scan.IngestionCompletedAt);
        Assert.Equal(first, scan.AnalysisCompletedAt);
    }
}
