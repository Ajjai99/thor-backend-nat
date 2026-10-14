using Thor.DataLayer.Models.Tenants;

namespace Thor.DataLayer.Repositories;

public interface IScanRepository : IRepository<Scan>
{
    /// <summary>
    /// Stamps <see cref="Scan.IngestionCompletedAt"/> and <see cref="Scan.AnalysisCompletedAt"/>,
    /// each only if still unset — a single statement, so concurrent callers never move a timestamp
    /// that is already recorded.
    /// </summary>
    Task StampAnalysisCompletedAsync(Guid scanId, DateTimeOffset completedAt, CancellationToken cancellationToken = default);

    /// <summary>
    /// Stamps <see cref="Scan.IngestionCompletedAt"/> only if still unset — a single statement, so
    /// concurrent callers never move a timestamp that is already recorded.
    /// </summary>
    Task StampIngestionCompletedAsync(Guid scanId, DateTimeOffset completedAt, CancellationToken cancellationToken = default);

    /// <summary>
    /// Marks <paramref name="scanId"/> as <see cref="ScanStatus.Failed"/> — any one task failing
    /// fails the whole scan outright, regardless of its sibling tasks' state. A no-op once the
    /// scan is already terminal (<see cref="ScanStatus.Failed"/> or <see cref="ScanStatus.Completed"/>).
    /// </summary>
    Task MarkFailedAsync(Guid scanId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Atomically increments <see cref="Scan.CompletedTasks"/> by one. Called once per
    /// <see cref="ScanTask"/> that reaches <see cref="ScanTaskStatus.Completed"/> — safe against
    /// double-counting because the caller only reaches here after a task-level conditional update
    /// that fires at most once per task (see <see cref="ScanTaskRepository.UpdateStatusAsync"/>).
    /// </summary>
    Task IncrementCompletedTasksAsync(Guid scanId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Marks <paramref name="scanId"/> as <see cref="ScanStatus.Completed"/> if — and only if —
    /// every <see cref="ScanTask"/> belonging to it is already <see cref="ScanTaskStatus.Completed"/>.
    /// A no-op once the scan is already terminal.
    /// </summary>
    Task CompleteIfAllTasksCompletedAsync(Guid scanId, CancellationToken cancellationToken = default);
}
