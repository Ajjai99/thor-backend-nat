using Thor.DataConnectionManager;
using Thor.DataLayer.Models.Tenants;
using Thor.DataLayer.Repositories;
using Thor.S3;

namespace Thor.CreateManifest.Core;

/// <summary>
/// Opens the tenant's database per call (see <see cref="TenantScanManifestStore"/>) and delegates
/// to <see cref="ScanFileRepository"/>.
/// </summary>
public sealed class TenantScanFileStore(ITenantConnectionManager tenantConnectionManager) : IScanFileStore
{
    public async Task MarkReceivedAsync(Guid tenantId, Guid scanId, IReadOnlyList<string> keys, CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;

        // A key with no task id did not come through the presign path, so it has no row to upgrade
        // and no task to attribute a new one to — the same rule ingestion applies.
        var rows = keys
            .Select(key => (Key: key, TaskId: UploadKeyParser.TryParse(key)?.TaskId))
            .Where(k => k.TaskId is not null)
            .Select(k => new ScanFile
            {
                Id = Guid.NewGuid(),
                ScanId = scanId,
                ScanTaskId = k.TaskId!.Value,
                FileLocation = k.Key,
                Status = ScanFileStatus.Received,
                CreatedAt = now,
                UpdatedAt = now,
            })
            .ToList();

        if (rows.Count == 0)
        {
            return;
        }

        await using var db = await tenantConnectionManager.GetTenantDbContextAsync(tenantId, cancellationToken);
        await new ScanFileRepository(db).MarkReceivedAsync(rows, cancellationToken);
    }
}
