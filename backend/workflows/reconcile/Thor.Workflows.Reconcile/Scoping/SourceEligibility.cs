using Microsoft.EntityFrameworkCore;
using Thor.DataLayer.Data;
using Thor.DataLayer.Models.Tenants;
using Thor.Workflows.Abstractions;

namespace Thor.Workflows.Reconcile.Scoping;

/// <summary>A source the sweep may act on, with the threshold that governs how much of it may vanish unattended.</summary>
internal sealed record SourceScope(Guid SourceId, string Name, short ThresholdPercent);

/// <summary>A source deliberately left alone, and why — recorded as a <see cref="ReconcileOutcome.Skipped"/> decision.</summary>
internal sealed record SkippedSource(Guid SourceId, string Name, short ThresholdPercent, string Reason);

internal sealed record RunEligibility(bool LedgerPresent, IReadOnlyList<SourceScope> Sweepable, IReadOnlyList<SkippedSource> Skipped);

/// <summary>Values of <see cref="ReconcileDecision.SkipReason"/>.</summary>
internal static class SkipReasons
{
    /// <summary>The scan's configuration names the source, but it has no task in the scan.</summary>
    public const string NoScanTask = "no_scan_task";

    /// <summary>A task of the source did not complete, so it did not deliver a full export.</summary>
    public const string TaskNotSuccessful = "task_not_successful";

    /// <summary>A later scan already covers the source; it is the one entitled to sweep it.</summary>
    public const string SupersededByNewerScan = "superseded_by_newer_scan";
}

/// <summary>
/// Decides what, if anything, a run may touch. These guards are the module's safety story: everything
/// downstream acts on an entity being <em>missing</em>, and missing has an innocent explanation for every
/// guilty one. The run-level ones throw — the run was started at the wrong moment and a module that
/// deletes data should be loud about it — except the empty ledger, which is reported, not thrown.
/// </summary>
internal sealed class SourceEligibility(TenantDbContext db)
{
    public async Task<RunEligibility> EvaluateAsync(Guid scanId, CancellationToken cancellationToken)
    {
        var scan = await db.Scans.AsNoTracking().FirstOrDefaultAsync(s => s.Id == scanId, cancellationToken)
            ?? throw new InvalidOperationException($"Scan {scanId} does not exist.");

        // A mid-scan sweep would read every source that has not delivered yet as wholly absent.
        if (scan.IngestionCompletedAt is null)
        {
            throw new InvalidOperationException(
                $"Scan {scanId} has not completed ingestion; absence from it means nothing yet. Reconcile runs behind the scan-completion gate.");
        }

        await EnsureGraphLoadNotStartedAsync(db, scanId, cancellationToken);

        // No ledger, no evidence: every entity would read as absent. Expected for a scan promoted before the
        // ledger existed or one that delivered nothing; anything else means promotion never recorded it.
        if (!await db.ScanSeenEntities.AnyAsync(s => s.ScanId == scanId, cancellationToken))
        {
            return new RunEligibility(LedgerPresent: false, [], []);
        }

        return await ScopeSourcesAsync(scanId, cancellationToken);
    }

    /// <summary>
    /// The graph load writes this scan's deletions into Neptune, and it starts only once this run is over.
    /// A run resumed after that — a redrive of a failed one — would delete rows whose vertices nothing will
    /// ever drop. Refused instead: the entities are still absent, so the next scan's run deletes them.
    /// Checked by every step that writes, not only the first, because a redrive resumes mid-run.
    /// </summary>
    public static async Task EnsureGraphLoadNotStartedAsync(TenantDbContext db, Guid scanId, CancellationToken cancellationToken)
    {
        if (await db.Workflows.AnyAsync(w => w.ScanId == scanId && w.WorkflowType == WorkflowTypes.GraphLoad, cancellationToken))
        {
            throw new InvalidOperationException(
                $"Scan {scanId}'s graph load has already started, so deletions made now would never reach the graph. " +
                "Nothing more is deleted for this scan; entities still absent are reconciled by the next scan.");
        }
    }

    /// <summary>
    /// Per source: every one of its tasks <em>completed</em> — the scan gate lets a scan finish with a failed
    /// task so one bad source cannot stall a tenant, and this is what stops that source being swept — and no
    /// later scan covers it. Sources come from the scan's configuration rather than its tasks, so a source
    /// that was meant to run and produced no task at all shows up as skipped instead of vacuously passing.
    /// </summary>
    private async Task<RunEligibility> ScopeSourcesAsync(Guid scanId, CancellationToken cancellationToken)
    {
        var sources = await db.Scans.AsNoTracking()
            .Where(s => s.Id == scanId)
            .SelectMany(s => s.ScanConfig.SourceMappings)
            .Where(m => m.Source != null)
            .Select(m => new { m.Source!.Id, m.Source.Name, m.Source.AutoDeleteThresholdPercent })
            .Distinct()
            .ToListAsync(cancellationToken);

        var tasks = await db.Tasks.AsNoTracking()
            .Where(t => t.ScanId == scanId)
            .Select(t => new { t.SourceId, t.Status, t.CreatedAt })
            .ToListAsync(cancellationToken);
        var tasksBySource = tasks.ToLookup(t => t.SourceId);

        var sweepable = new List<SourceScope>();
        var skipped = new List<SkippedSource>();

        foreach (var source in sources)
        {
            var ownTasks = tasksBySource[source.Id].ToList();
            string? reason = null;

            if (ownTasks.Count == 0)
            {
                reason = SkipReasons.NoScanTask;
            }
            else if (ownTasks.Any(t => t.Status != ScanTaskStatus.Completed))
            {
                reason = SkipReasons.TaskNotSuccessful;
            }
            else
            {
                // A later scan may have seen entities this one did not — created in between — and may already
                // have been reconciled and dropped its ledger. Only the latest scan of a source may sweep it.
                var latest = ownTasks.Max(t => t.CreatedAt);
                if (await db.Tasks.AnyAsync(t => t.SourceId == source.Id && t.ScanId != scanId && t.CreatedAt > latest, cancellationToken))
                {
                    reason = SkipReasons.SupersededByNewerScan;
                }
            }

            if (reason is null)
            {
                sweepable.Add(new SourceScope(source.Id, source.Name, source.AutoDeleteThresholdPercent));
            }
            else
            {
                skipped.Add(new SkippedSource(source.Id, source.Name, source.AutoDeleteThresholdPercent, reason));
            }
        }

        return new RunEligibility(LedgerPresent: true, sweepable, skipped);
    }
}
