namespace Thor.ScanGate.Core;

/// <summary>Tenant-scoped reads and writes the gate needs.</summary>
public interface IScanGateStore
{
    /// <summary>The scan a manifest belongs to, or null if the manifest does not exist.</summary>
    Task<Guid?> FindScanIdForManifestAsync(Guid tenantId, Guid scanManifestId, CancellationToken cancellationToken = default);

    Task<ScanGateSnapshot> LoadAsync(Guid tenantId, Guid scanId, CancellationToken cancellationToken = default);

    Task MarkAbandonedAsync(Guid tenantId, IReadOnlyList<Guid> scanFileIds, CancellationToken cancellationToken = default);

    /// <summary>Stamps the scan's <c>ingestion_completed_at</c>, keeping the first time if it is already set.</summary>
    Task StampIngestionCompletedAsync(Guid tenantId, Guid scanId, DateTimeOffset completedAt, CancellationToken cancellationToken = default);

    Task StampCompletedAsync(Guid tenantId, Guid scanId, DateTimeOffset completedAt, CancellationToken cancellationToken = default);
}
