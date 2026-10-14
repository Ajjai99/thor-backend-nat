using Npgsql;
using Microsoft.EntityFrameworkCore;
using Thor.DataLayer.Data;
using Thor.DataLayer.Models.Tenants;

namespace Thor.DataLayer.Repositories;

public sealed class ScanRepository(TenantDbContext context)
    : Repository<Scan>(context), IScanRepository
{
    public async Task StampAnalysisCompletedAsync(Guid scanId, DateTimeOffset completedAt, CancellationToken cancellationToken = default)
    {
        const string Sql = """
            UPDATE tenant.scan
            SET ingestion_completed_at = COALESCE(ingestion_completed_at, @completed_at),
                analysis_completed_at = COALESCE(analysis_completed_at, @completed_at)
            WHERE id = @id
            """;

        List<NpgsqlParameter> parameters = [new("id", scanId), new("completed_at", completedAt)];

        await TenantSqlExec.ExecAsync(context, Sql, parameters, cancellationToken);
    }
    
    public async Task StampIngestionCompletedAsync(Guid scanId, DateTimeOffset completedAt, CancellationToken cancellationToken = default)
    {
        const string Sql = """
            UPDATE tenant.scan
            SET ingestion_completed_at = COALESCE(ingestion_completed_at, @completed_at)
            WHERE id = @id
            """;

        List<NpgsqlParameter> parameters = [new("id", scanId), new("completed_at", completedAt)];

        await TenantSqlExec.ExecAsync(context, Sql, parameters, cancellationToken);
    }

    public async Task MarkFailedAsync(Guid scanId, CancellationToken cancellationToken = default)
    {
        await context.Scans
            .Where(s => s.Id == scanId && s.Status != ScanStatus.Failed && s.Status != ScanStatus.Completed)
            .ExecuteUpdateAsync(s => s
                .SetProperty(sc => sc.Status, ScanStatus.Failed)
                .SetProperty(sc => sc.CompletedAt, DateTimeOffset.UtcNow), cancellationToken);
    }

    public async Task IncrementCompletedTasksAsync(Guid scanId, CancellationToken cancellationToken = default)
    {
        await context.Scans
            .Where(s => s.Id == scanId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(sc => sc.CompletedTasks, sc => sc.CompletedTasks + 1), cancellationToken);
    }

    // A single atomic UPDATE ... WHERE NOT EXISTS(...): whichever task is the last to have its
    // own status commit to Completed is guaranteed to see every sibling task (including itself)
    // as Completed when this runs immediately after, since each task's commit happens-before this
    // check for that same call. No explicit transaction or lock needed.
    public async Task CompleteIfAllTasksCompletedAsync(Guid scanId, CancellationToken cancellationToken = default)
    {
        await context.Scans
            .Where(s => s.Id == scanId
                && s.Status != ScanStatus.Completed
                && s.Status != ScanStatus.Failed
                && !context.Tasks.Any(t => t.ScanId == scanId && t.Status != ScanTaskStatus.Completed))
            .ExecuteUpdateAsync(s => s
                .SetProperty(sc => sc.Status, ScanStatus.Completed)
                .SetProperty(sc => sc.CompletedAt, DateTimeOffset.UtcNow), cancellationToken);
    }
}
