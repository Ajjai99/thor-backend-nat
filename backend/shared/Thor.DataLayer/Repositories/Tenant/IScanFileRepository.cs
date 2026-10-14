using Thor.DataLayer.Models.Tenants;

namespace Thor.DataLayer.Repositories;

public interface IScanFileRepository : IRepository<ScanFile>
{
    /// <summary>
    /// Records every file as <see cref="ScanFileStatus.Received"/>: inserts it if no row exists, and
    /// upgrades a <see cref="ScanFileStatus.Pending"/> row registered at presign time. Any other
    /// status already written for that (scan, file) is kept — callers are re-invoked on retry and
    /// on at-least-once SQS delivery, and must never reset a file that has since been ingested or
    /// failed.
    /// </summary>
    Task MarkReceivedAsync(IReadOnlyList<ScanFile> files, CancellationToken cancellationToken = default);

    /// <summary>
    /// Registers a file as <see cref="ScanFileStatus.Pending"/> when its upload URL is presigned,
    /// with the URL's expiry. A no-op if the (scan, file) row already exists.
    /// </summary>
    Task RegisterPendingAsync(ScanFile file, CancellationToken cancellationToken = default);

    /// <summary>Number of this scan's files in each status.</summary>
    Task<IReadOnlyDictionary<string, int>> CountByStatusAsync(Guid scanId, CancellationToken cancellationToken = default);

    /// <summary>This scan's files still <see cref="ScanFileStatus.Pending"/>, untracked.</summary>
    Task<IReadOnlyList<ScanFile>> GetPendingAsync(Guid scanId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Marks these files <see cref="ScanFileStatus.Abandoned"/> — only those still
    /// <see cref="ScanFileStatus.Pending"/>, so a file whose object turned up in the meantime is
    /// never overwritten.
    /// </summary>
    Task MarkAbandonedAsync(IReadOnlyList<Guid> ids, CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes this file's outcome, overwriting whatever was there. Last write wins on purpose: a
    /// Map item that fails and is then retried successfully must end up Ingested, and status is a
    /// column keyed by file rather than a counter precisely so it can converge that way. Inserts the
    /// row if <see cref="MarkReceivedAsync"/> never ran for it.
    /// </summary>
    Task MarkStatusAsync(ScanFile file, CancellationToken cancellationToken = default);
}
