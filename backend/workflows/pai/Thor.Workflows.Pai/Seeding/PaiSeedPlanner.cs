using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;
using Thor.DataLayer.Data;
using Thor.Graph;
using Thor.Workflows.Pai.Constants;
using Thor.Workflows.Pai.Persistence;
using Thor.Workflows.Pai.Privilege;

namespace Thor.Workflows.Pai.Seeding;

internal sealed record SeedSummary(int Definitions, int FullDefinitions, int WindowScans, int ComputeSeeds, int RetireSeeds);

/// <summary>
/// Decides what one run recomputes, and stages it: <c>pai_run_definition</c> (full or incremental per
/// definition), <c>pai_run_scan</c> (the change window), <c>pai_seed</c> (targets to compute or
/// retire) and a depth-0 <c>pai_group_path</c> per computed target, which expand walks down from.
///
/// <para><b>Full</b> for a definition whose hash differs from the one its last completed run used —
/// new, edited, or never completed. Every matching target is seeded, by a label match in the graph.</para>
///
/// <para><b>Incremental</b> otherwise, seeded from the change events of every scan no completed run
/// has covered (a failed run's scans roll into the next). Three sources, because each catches changes
/// the others cannot see:</para>
/// <list type="number">
/// <item>Edge events of the definition's relationship. The container (the edge's <c>to</c> side) is
/// walked <em>up</em> to the targets it sits under — a member added to Helpdesk, nested in Domain
/// Admins, changes an edge whose container matches no pattern, so only its ancestor is a usable seed.</item>
/// <item>Target entity events. A target that matches now is recomputed (renamed into the pattern, or
/// its paths were cut short last time); one that no longer matches but still has routes is retired.</item>
/// <item>Routes through a group that changed. A group that vanished cannot be walked up from, so the
/// routes already materialised through it (<c>pai_result.via_node_ids</c>) name the targets to redo.</item>
/// </list>
/// </summary>
internal sealed class PaiSeedPlanner(TenantDbContext db, IGraphReader graph, Guid tenantId, Guid runId, ILogger logger)
{
    public async Task<SeedSummary> PlanAsync(Guid? scanId, CancellationToken cancellationToken)
    {
        await ResetAsync(cancellationToken);
        await RecordDefinitionsAsync(cancellationToken);
        var windowScans = await RecordWindowAsync(scanId, cancellationToken);

        var definitions = await RunDefinition.LoadAsync(db, runId, cancellationToken);
        var incremental = definitions.Values.Where(d => !d.IsFull).ToList();
        var changes = incremental.Count > 0 && windowScans.Length > 0
            ? await ChangeSet.ReadAsync(db, graph, tenantId, windowScans, incremental, logger, cancellationToken)
            : ChangeSet.Empty;

        var computeTotal = 0;
        var retireTotal = 0;
        foreach (var definition in definitions.Values)
        {
            var (compute, retire) = definition.IsFull
                ? (await graph.MatchingTargetsAsync(tenantId, definition.Pattern, candidateIds: null, cancellationToken), [])
                : await IncrementalSeedsAsync(definition, changes, cancellationToken);

            await StageSeedsAsync(definition.Id, compute, retire, cancellationToken);
            computeTotal += compute.Count;
            retireTotal += retire.Count;

            logger.LogInformation(
                "PAI run {RunId}, definition {Definition} ({Mode}): {Compute} target(s) to compute, {Retire} to retire.",
                runId, definition.Name, definition.IsFull ? "full" : "incremental", compute.Count, retire.Count);
        }

        return new SeedSummary(definitions.Count, definitions.Count - incremental.Count, windowScans.Length, computeTotal, retireTotal);
    }

    /// <summary>
    /// Drops whatever an earlier execution with this run id staged, so every start plans from scratch.
    /// Resuming belongs inside an execution (expand's loop, a redrive from the failed state), neither
    /// of which re-runs this step.
    /// </summary>
    private async Task ResetAsync(CancellationToken cancellationToken)
    {
        foreach (var table in new[] { "pai_seed", "pai_group_path", "pai_run_definition", "pai_run_scan" })
        {
            await PaiSql.ExecAsync(db, $"DELETE FROM tenant.{table} WHERE run_id = @run", [Run()], cancellationToken);
        }
    }

    /// <summary>Fixes each active definition's mode and hash now, with the database's clock as the run's start.</summary>
    private Task RecordDefinitionsAsync(CancellationToken cancellationToken) =>
        PaiSql.ExecAsync(db, """
            INSERT INTO tenant.pai_run_definition (run_id, definition_id, is_full, definition_hash, run_started_at)
            SELECT @run, d.id, d.computed_hash IS DISTINCT FROM d.definition_hash, d.definition_hash, now()
            FROM tenant.privilege_definition d
            WHERE d.is_active
            """, [Run()], cancellationToken);

    /// <summary>
    /// The scans whose changes this run reads: this run's own scan, plus every scan through the gate
    /// (<c>analysis_completed_at</c> set) that no completed run has read. A scan still ingesting is
    /// left for a later run, so it is never marked covered on partial events.
    /// </summary>
    private async Task<Guid[]> RecordWindowAsync(Guid? scanId, CancellationToken cancellationToken)
    {
        var scan = new NpgsqlParameter("scan", NpgsqlDbType.Uuid) { Value = (object?)scanId ?? DBNull.Value };
        var rows = await PaiSql.GuidsAsync(db, """
            INSERT INTO tenant.pai_run_scan (run_id, scan_id)
            SELECT @run, s.id
            FROM tenant.scan s
            WHERE (s.id = @scan OR s.analysis_completed_at IS NOT NULL)
              AND NOT EXISTS (
                  SELECT 1
                  FROM tenant.pai_run_scan covered
                  JOIN tenant.workflow w ON w.run_id = covered.run_id AND w.workflow_type = 'pai' AND w.status = 'completed'
                  WHERE covered.scan_id = s.id)
            RETURNING scan_id
            """, [Run(), scan], cancellationToken);
        return [.. rows];
    }

    private async Task<(IReadOnlyList<Guid> Compute, IReadOnlyList<Guid> Retire)> IncrementalSeedsAsync(
        RunDefinition definition, ChangeSet changes, CancellationToken cancellationToken)
    {
        var pattern = definition.Pattern;
        var compute = new HashSet<Guid>();

        // 1. Changed edges of this relationship, walked up to the targets above them.
        if (changes.ReachedFromEdges.TryGetValue(pattern.EdgeType, out var reached))
        {
            var candidates = reached.Where(node => node.Label == pattern.TargetLabel).Select(node => node.Id).ToList();
            if (candidates.Count > 0)
            {
                compute.UnionWith(await graph.MatchingTargetsAsync(tenantId, pattern, candidates, cancellationToken));
            }
        }

        // 2. Changed targets: recompute what matches, retire what stopped matching.
        var retire = new List<Guid>();
        if (changes.EntitiesByLabel.TryGetValue(pattern.TargetLabel, out var changedTargets) && changedTargets.Count > 0)
        {
            var matching = await graph.MatchingTargetsAsync(tenantId, pattern, changedTargets, cancellationToken);
            compute.UnionWith(matching);

            var stopped = changedTargets.Except(matching).ToArray();
            if (stopped.Length > 0)
            {
                retire = await PaiSql.GuidsAsync(db, """
                    SELECT DISTINCT target_id FROM tenant.pai_result
                    WHERE privilege_definition_id = @definition AND target_id = ANY(@targets)
                    """, [new NpgsqlParameter("definition", definition.Id), new NpgsqlParameter("targets", stopped)], cancellationToken);
            }
        }

        // 3. Routes through a group that changed or vanished.
        if (changes.EntitiesByLabel.TryGetValue(GraphLabels.Group, out var changedGroups) && changedGroups.Count > 0)
        {
            compute.UnionWith(await PaiSql.GuidsAsync(db, """
                SELECT DISTINCT target_id FROM tenant.pai_result
                WHERE privilege_definition_id = @definition AND via_node_ids && @groups
                """, [new NpgsqlParameter("definition", definition.Id), new NpgsqlParameter("groups", changedGroups.ToArray())], cancellationToken));
        }

        // Retiring wins: a target that no longer matches has no routes to compute.
        compute.ExceptWith(retire);
        return ([.. compute], retire);
    }

    private async Task StageSeedsAsync(Guid definitionId, IReadOnlyList<Guid> compute, IReadOnlyList<Guid> retire, CancellationToken cancellationToken)
    {
        const string seedSql = """
            INSERT INTO tenant.pai_seed (run_id, definition_id, target_id, action)
            SELECT @run, @definition, target, @action FROM unnest(@targets) AS target
            ON CONFLICT DO NOTHING
            """;

        if (compute.Count > 0)
        {
            await PaiSql.ExecAsync(db, seedSql, [.. TargetParameters(definitionId, compute), new NpgsqlParameter("action", SeedActions.Compute)], cancellationToken);
            await PaiSql.ExecAsync(db, """
                INSERT INTO tenant.pai_group_path (run_id, definition_id, target_id, depth, last_node_id, node_ids, failed)
                SELECT @run, @definition, target, 0, target, ARRAY[target], FALSE FROM unnest(@targets) AS target
                ON CONFLICT DO NOTHING
                """, TargetParameters(definitionId, compute), cancellationToken);
        }

        if (retire.Count > 0)
        {
            await PaiSql.ExecAsync(db, seedSql, [.. TargetParameters(definitionId, retire), new NpgsqlParameter("action", SeedActions.Retire)], cancellationToken);
        }
    }

    private NpgsqlParameter[] TargetParameters(Guid definitionId, IReadOnlyList<Guid> targets) =>
        [Run(), new NpgsqlParameter("definition", definitionId), new NpgsqlParameter("targets", targets.ToArray())];

    private NpgsqlParameter Run() => new("run", runId);
}
