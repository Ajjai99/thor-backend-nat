using Thor.DataConnectionManager;
using Thor.DataLayer.Repositories;
using Thor.Workflows.Abstractions;

namespace Thor.ScanGate.Core;

/// <summary>
/// Opens the tenant's database per call — <see cref="ITenantConnectionManager"/> resolves routing
/// against the Master metadata store first, so an unknown tenant fails closed before any read —
/// and delegates to the DataLayer repositories.
/// </summary>
public sealed class TenantScanGateStore(ITenantConnectionManager tenantConnectionManager) : IScanGateStore
{
    public async Task<Guid?> FindScanIdForManifestAsync(Guid tenantId, Guid scanManifestId, CancellationToken cancellationToken = default)
    {
        await using var db = await tenantConnectionManager.GetTenantDbContextAsync(tenantId, cancellationToken);
        var manifest = await new ScanManifestRepository(db).GetByIdAsync(scanManifestId, cancellationToken);
        return manifest?.ScanId;
    }

    public async Task<ScanGateSnapshot> LoadAsync(Guid tenantId, Guid scanId, CancellationToken cancellationToken = default)
    {
        await using var db = await tenantConnectionManager.GetTenantDbContextAsync(tenantId, cancellationToken);
        var scanFiles = new ScanFileRepository(db);

        var tasks = await new ScanTaskRepository(db).GetByScanIdAsync(scanId, cancellationToken);
        var fileCounts = await scanFiles.CountByStatusAsync(scanId, cancellationToken);
        var pendingFiles = await scanFiles.GetPendingAsync(scanId, cancellationToken);
        var manifests = await new ScanManifestRepository(db).GetByScanAsync(scanId, cancellationToken);
        var workflowRepository = new WorkflowRepository(db);
        var workflows = await workflowRepository.GetForManifestsOfScanAsync(scanId, cancellationToken);
        var scanWorkflows = await workflowRepository.GetByScanIdAsync(scanId, cancellationToken);
        var reconciles = scanWorkflows.Where(w => w.WorkflowType == WorkflowTypes.Reconcile).ToList();
        var graphLoads = scanWorkflows.Where(w => w.WorkflowType == WorkflowTypes.GraphLoad).ToList();

        return new ScanGateSnapshot(tasks, fileCounts, pendingFiles, [.. manifests.Select(m => m.Id)], workflows, reconciles, graphLoads);
    }

    public async Task MarkAbandonedAsync(Guid tenantId, IReadOnlyList<Guid> scanFileIds, CancellationToken cancellationToken = default)
    {
        await using var db = await tenantConnectionManager.GetTenantDbContextAsync(tenantId, cancellationToken);
        await new ScanFileRepository(db).MarkAbandonedAsync(scanFileIds, cancellationToken);
    }

    public async Task StampIngestionCompletedAsync(Guid tenantId, Guid scanId, DateTimeOffset completedAt, CancellationToken cancellationToken = default)
    {
        await using var db = await tenantConnectionManager.GetTenantDbContextAsync(tenantId, cancellationToken);
        await new ScanRepository(db).StampIngestionCompletedAsync(scanId, completedAt, cancellationToken);
    }

    public async Task StampCompletedAsync(Guid tenantId, Guid scanId, DateTimeOffset completedAt, CancellationToken cancellationToken = default)
    {
        await using var db = await tenantConnectionManager.GetTenantDbContextAsync(tenantId, cancellationToken);
        await new ScanRepository(db).StampAnalysisCompletedAsync(scanId, completedAt, cancellationToken);
    }
}
