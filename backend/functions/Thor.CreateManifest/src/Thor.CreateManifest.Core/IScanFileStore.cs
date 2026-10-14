namespace Thor.CreateManifest.Core;

/// <summary>Tenant-scoped writes to <see cref="Thor.DataLayer.Models.Tenants.ScanFile"/> rows.</summary>
public interface IScanFileStore
{
    /// <summary>
    /// Records these uploaded keys as received — upgrading the Pending rows Thor.TaskApi registered
    /// at presign time, never overwriting a status ingestion has since written.
    /// </summary>
    Task MarkReceivedAsync(Guid tenantId, Guid scanId, IReadOnlyList<string> keys, CancellationToken cancellationToken = default);
}
