using Microsoft.Extensions.Logging;
using Npgsql;
using Thor.DataLayer.Data;
using Thor.DataLayer.Models.Tenants;
using Thor.Workflows.Reconcile.Persistence;
using Thor.Workflows.Reconcile.Scoping;

namespace Thor.Workflows.Reconcile.Reconciliation;

/// <summary>
/// Decides, per (source, entity type), what this scan's absences mean, and acts on everything except the
/// deletions themselves: withdraws proposals for rows the scan saw again, measures absence against the
/// source's threshold, flags the absent rows when it is exceeded, and otherwise stages them in
/// <c>reconcile_pending</c> for the delete waves. Every decision — skipped sources included — is
/// recorded in <c>reconcile_decision</c>.
///
/// <para>The whole feature rests on connector exports being full dumps: a live row of a source whose
/// tasks all completed, absent from the scan's ledger, is one the source no longer has.</para>
/// </summary>
internal sealed class AbsencePlanner(TenantDbContext db, Guid runId, Guid scanId, ILogger logger)
{
    /// <summary>Stages the run's deletions and records its decisions. Returns how many deletions were staged.</summary>
    public async Task<long> PlanAsync(RunEligibility eligibility, CancellationToken cancellationToken)
    {
        // Start-run is retried whole by the state machine, so it starts from a clean slate each time.
        await ReconcileSql.ExecAsync(db, "DELETE FROM tenant.reconcile_pending WHERE run_id = @run", [Run()], cancellationToken);
        await ReconcileSql.ExecAsync(db, "DELETE FROM tenant.reconcile_decision WHERE run_id = @run", [Run()], cancellationToken);

        var decisions = new List<ReconcileDecision>();

        foreach (var skipped in eligibility.Skipped)
        {
            // Individually: which source went unswept is what an operator acts on.
            logger.LogWarning(
                "Source {SourceName} ({SourceId}) not swept for scan {ScanId}: {Reason}. Its entities are absent because it did not deliver, not because they were deleted.",
                skipped.Name, skipped.SourceId, scanId, skipped.Reason);

            decisions.AddRange(EntityTypes.All.Select(type =>
                Decision(skipped.SourceId, type, ReconcileOutcome.Skipped, skipped.ThresholdPercent, skipReason: skipped.Reason)));
        }

        long staged = 0;
        foreach (var source in eligibility.Sweepable)
        {
            foreach (var type in EntityTypes.All)
            {
                var withdrawn = await WithdrawProposalsAsync(source.SourceId, type, cancellationToken);
                var (live, absent) = await MeasureAsync(source.SourceId, type, cancellationToken);

                string outcome;
                if (absent == 0)
                {
                    outcome = ReconcileOutcome.NothingAbsent;
                }
                else if (ExceedsThreshold(absent, live, source.ThresholdPercent))
                {
                    outcome = ReconcileOutcome.Proposed;
                    var newlyProposed = await ProposeAsync(source.SourceId, type, cancellationToken);
                    logger.LogWarning(
                        "Source {SourceName} ({SourceId}) lost {AbsentCount} of {LiveCount} {EntityType} row(s) in scan {ScanId}, over its {Threshold}% threshold: proposing, not deleting ({NewlyProposed} newly proposed).",
                        source.Name, source.SourceId, absent, live, type, scanId, source.ThresholdPercent, newlyProposed);
                }
                else
                {
                    outcome = ReconcileOutcome.Deleted;
                    staged += await StageAsync(source.SourceId, type, staged, cancellationToken);
                    logger.LogInformation(
                        "Source {SourceName} ({SourceId}) lost {AbsentCount} of {LiveCount} {EntityType} row(s) in scan {ScanId}, within its {Threshold}% threshold: staged for deletion.",
                        source.Name, source.SourceId, absent, live, type, scanId, source.ThresholdPercent);
                }

                decisions.Add(Decision(source.SourceId, type, outcome, source.ThresholdPercent, live, absent, withdrawn));
            }
        }

        db.ReconcileDecisions.AddRange(decisions);
        await db.SaveChangesAsync(cancellationToken);
        return staged;
    }

    /// <summary>
    /// Integer arithmetic on both sides — <c>absent / live</c> is not representable, and the boundary is the
    /// one place a rounded ratio must not decide whether data is destroyed. Equality is within the
    /// threshold, and a threshold of 0 makes any absence exceed it: "never auto-delete".
    /// </summary>
    internal static bool ExceedsThreshold(long absent, long live, short thresholdPercent) =>
        absent * 100L > thresholdPercent * live;

    /// <summary>
    /// Live rows and how many of them the scan did not see. Both sides exclude already-deleted rows, and
    /// that symmetry matters: counting tombstones in the denominator only would let the ratio drift until a
    /// healthy source permanently read as over threshold.
    /// </summary>
    private async Task<(long Live, long Absent)> MeasureAsync(Guid sourceId, string type, CancellationToken cancellationToken)
    {
        var rows = await ReconcileSql.QueryAsync(db, $"""
            SELECT count(*), count(*) FILTER (WHERE NOT {SeenByThisScan})
            FROM tenant.{type} e
            WHERE e.source_id = @source AND e.is_deleted = FALSE
            """,
            [Scan(), Source(sourceId), EntityType(type)],
            reader => (reader.GetInt64(0), reader.GetInt64(1)),
            cancellationToken);
        return rows[0];
    }

    /// <summary>A proposal is about the latest scan, so a row the scan saw again stops being proposed without anyone dismissing it.</summary>
    private Task<int> WithdrawProposalsAsync(Guid sourceId, string type, CancellationToken cancellationToken) =>
        ReconcileSql.ExecAsync(db, $"""
            UPDATE tenant.{type} e
            SET deletion_proposed_at = NULL, deletion_proposed_scan_id = NULL
            WHERE e.source_id = @source AND e.deletion_proposed_at IS NOT NULL AND {SeenByThisScan}
            """,
            [Scan(), Source(sourceId), EntityType(type)], cancellationToken);

    /// <summary>
    /// Flags the absent rows and changes nothing else — not <c>is_deleted</c>, not the edges, not the graph,
    /// no change events. Rows already flagged keep their first proposal, so a source that stays over its
    /// threshold costs nothing on later scans.
    /// </summary>
    private Task<int> ProposeAsync(Guid sourceId, string type, CancellationToken cancellationToken) =>
        ReconcileSql.ExecAsync(db, $"""
            UPDATE tenant.{type} e
            SET deletion_proposed_at = now(), deletion_proposed_scan_id = @scan
            WHERE e.source_id = @source AND e.is_deleted = FALSE AND e.deletion_proposed_at IS NULL
              AND NOT {SeenByThisScan}
            """,
            [Scan(), Source(sourceId), EntityType(type)], cancellationToken);

    /// <summary>Appends the absent rows to the run's staging, numbered on from <paramref name="after"/> so the run's <c>seq</c> stays contiguous.</summary>
    private Task<int> StageAsync(Guid sourceId, string type, long after, CancellationToken cancellationToken) =>
        ReconcileSql.ExecAsync(db, $"""
            INSERT INTO tenant.reconcile_pending (run_id, seq, entity_type, entity_id)
            SELECT @run, @after + row_number() OVER (ORDER BY e.id), @entity_type, e.id
            FROM tenant.{type} e
            WHERE e.source_id = @source AND e.is_deleted = FALSE AND NOT {SeenByThisScan}
            """,
            [Run(), Scan(), Source(sourceId), EntityType(type), new NpgsqlParameter("after", after)], cancellationToken);

    /// <summary>
    /// The anti-join every statement shares. <c>{type}</c> above is one of <see cref="EntityTypes"/> — internal
    /// constants, and the table names — so interpolating it is safe; everything else is a parameter.
    /// </summary>
    private const string SeenByThisScan = """
        EXISTS (
            SELECT 1 FROM tenant.scan_seen_entity s
            WHERE s.scan_id = @scan AND s.source_id = e.source_id
              AND s.entity_type = @entity_type AND s.native_id = e.native_id)
        """;

    private ReconcileDecision Decision(
        Guid sourceId, string type, string outcome, short threshold, long? live = null, long? absent = null, int withdrawn = 0, string? skipReason = null) =>
        new()
        {
            Id = Guid.NewGuid(), RunId = runId, ScanId = scanId, SourceId = sourceId, EntityType = type,
            Outcome = outcome, SkipReason = skipReason, LiveCount = live, AbsentCount = absent,
            ThresholdPercent = threshold, ProposalsWithdrawn = withdrawn, DecidedAt = DateTimeOffset.UtcNow,
        };

    private NpgsqlParameter Run() => new("run", runId);

    private NpgsqlParameter Scan() => new("scan", scanId);

    private static NpgsqlParameter Source(Guid sourceId) => new("source", sourceId);

    private static NpgsqlParameter EntityType(string type) => new("entity_type", type);
}
