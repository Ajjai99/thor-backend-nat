using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Npgsql;
using Thor.DataLayer.Data;
using Thor.Graph;
using Thor.Workflows.Pai.Constants;
using Thor.Workflows.Pai.Persistence;
using Thor.Workflows.Pai.Privilege;

namespace Thor.Workflows.Pai.Expansion;

internal sealed record ExpandProgress(int LevelsRun, int PathsStaged, bool IsComplete);

/// <summary>
/// Stages every simple path down the group layer from each computed target of a transitive
/// (<c>MEMBER_OF</c>) definition, one level per round: the paths at the deepest staged depth are the
/// frontier, one batched hop fetches the child groups of their last nodes, and each child not already
/// on its path extends it. Accounts are never walked here — they are the fan-out's final hop — so the
/// work is proportional to the group layer, which is far smaller than the account layer.
///
/// <para>Only groups are expanded because Neptune answers only bounded one-hop queries this way; a
/// single variable-length traversal down to accounts is what runs into its query timeout.</para>
///
/// <para>A level is inserted in one transaction, so the deepest staged depth is always a finished
/// level and an interrupted invocation resumes cleanly. Single-hop definitions stay at depth 0.</para>
/// </summary>
internal sealed class GroupPathExpander(
    TenantDbContext db, IGraphReader graph, Guid tenantId, Guid runId, int maxPathsPerTarget, ILogger logger)
{
    private const int InsertBatchSize = 2000;

    public async Task<ExpandProgress> RunAsync(IReadOnlyDictionary<Guid, RunDefinition> definitions, TimeSpan budget, CancellationToken cancellationToken)
    {
        var transitive = definitions.Values.Where(d => d.Pattern.IsTransitive).ToDictionary(d => d.Id);
        if (transitive.Count == 0)
        {
            return new ExpandProgress(0, 0, IsComplete: true);
        }

        var clock = Stopwatch.StartNew();
        var levelsRun = 0;
        var staged = 0;

        // At least one level per call, so repeated calls always make progress; the budget only
        // decides whether to start another.
        do
        {
            var added = await RunLevelAsync(transitive, cancellationToken);
            if (added is null)
            {
                return new ExpandProgress(levelsRun, staged, IsComplete: true);
            }

            levelsRun++;
            staged += added.Value;
        }
        while (clock.Elapsed < budget);

        return new ExpandProgress(levelsRun, staged, IsComplete: false);
    }

    /// <summary>Expands the deepest staged level. Null when there is nothing left to expand.</summary>
    private async Task<int?> RunLevelAsync(Dictionary<Guid, RunDefinition> transitive, CancellationToken cancellationToken)
    {
        var definitionIds = transitive.Keys.ToArray();
        var depths = await PaiSql.QueryAsync(db,
            "SELECT max(depth) FROM tenant.pai_group_path WHERE run_id = @run AND definition_id = ANY(@definitions)",
            [Run(), new NpgsqlParameter("definitions", definitionIds)],
            reader => reader.IsDBNull(0) ? (int?)null : reader.GetInt32(0),
            cancellationToken);
        if (depths[0] is not { } depth)
        {
            return null;
        }

        if (depth >= PaiWaves.DepthCap)
        {
            logger.LogWarning(
                "PAI run {RunId}: group paths reached the depth cap of {Cap}; any deeper nesting is not evaluated.", runId, PaiWaves.DepthCap);
            return null;
        }

        var frontier = await PaiSql.QueryAsync(db, """
            SELECT definition_id, target_id, node_ids
            FROM tenant.pai_group_path
            WHERE run_id = @run AND depth = @depth AND definition_id = ANY(@definitions)
            """,
            [Run(), new NpgsqlParameter("depth", depth), new NpgsqlParameter("definitions", definitionIds)],
            reader => (DefinitionId: reader.GetGuid(0), TargetId: reader.GetGuid(1), NodeIds: reader.GetFieldValue<Guid[]>(2)),
            cancellationToken);

        // A depth-0 path ends at the target, whatever its label; deeper paths end at a group.
        var lastNodes = frontier
            .Select(path => new GraphNodeRef(path.NodeIds[^1], depth == 0 ? transitive[path.DefinitionId].Pattern.TargetLabel : GraphLabels.Group))
            .ToHashSet();
        var children = (await graph.InNeighboursAsync(tenantId, lastNodes, PrivilegePattern.MemberOf, GraphLabels.Group, cancellationToken))
            .ToLookup(hop => hop.NodeId, hop => hop.NeighbourId);

        var pathCounts = await PathCountsAsync(cancellationToken);
        var truncated = new HashSet<(Guid, Guid)>();
        var extended = new List<(Guid DefinitionId, Guid TargetId, Guid[] NodeIds)>();

        foreach (var path in frontier)
        {
            var key = (path.DefinitionId, path.TargetId);
            foreach (var child in children[path.NodeIds[^1]])
            {
                // Simple paths only: a child already on the path is a cycle.
                if (Array.IndexOf(path.NodeIds, child) >= 0)
                {
                    continue;
                }

                var count = pathCounts.GetValueOrDefault(key);
                if (count >= maxPathsPerTarget)
                {
                    truncated.Add(key);
                    continue;
                }

                pathCounts[key] = count + 1;
                extended.Add((path.DefinitionId, path.TargetId, [.. path.NodeIds, child]));
            }
        }

        foreach (var (definitionId, targetId) in truncated)
        {
            logger.LogWarning(
                "PAI run {RunId}: target {TargetId} of definition {Definition} reached {Cap} group paths; further paths are not evaluated.",
                runId, targetId, transitive[definitionId].Name, maxPathsPerTarget);
        }

        if (extended.Count == 0)
        {
            return null;
        }

        await using (var transaction = await db.Database.BeginTransactionAsync(cancellationToken))
        {
            foreach (var batch in extended.Chunk(InsertBatchSize))
            {
                await PaiSql.ExecAsync(db, """
                    INSERT INTO tenant.pai_group_path (run_id, definition_id, target_id, depth, last_node_id, node_ids, failed)
                    SELECT @run, p.definition_id, p.target_id, @depth, p.last_node_id, string_to_array(p.node_ids, ',')::uuid[], FALSE
                    FROM unnest(@definition_ids, @target_ids, @last_node_ids, @node_ids) AS p(definition_id, target_id, last_node_id, node_ids)
                    ON CONFLICT DO NOTHING
                    """,
                    [
                        Run(),
                        new NpgsqlParameter("depth", depth + 1),
                        new NpgsqlParameter("definition_ids", batch.Select(p => p.DefinitionId).ToArray()),
                        new NpgsqlParameter("target_ids", batch.Select(p => p.TargetId).ToArray()),
                        new NpgsqlParameter("last_node_ids", batch.Select(p => p.NodeIds[^1]).ToArray()),
                        // Ragged uuid[][] cannot go through unnest, so each path travels as text.
                        new NpgsqlParameter("node_ids", batch.Select(p => string.Join(',', p.NodeIds)).ToArray()),
                    ],
                    cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
        }

        logger.LogInformation(
            "PAI run {RunId}: expanded depth {Depth} ({Frontier} path(s)) into {Extended} path(s) at depth {Next}.",
            runId, depth, frontier.Count, extended.Count, depth + 1);
        return extended.Count;
    }

    private async Task<Dictionary<(Guid, Guid), int>> PathCountsAsync(CancellationToken cancellationToken)
    {
        var rows = await PaiSql.QueryAsync(db,
            "SELECT definition_id, target_id, count(*)::int FROM tenant.pai_group_path WHERE run_id = @run GROUP BY definition_id, target_id",
            [Run()],
            reader => (Key: (reader.GetGuid(0), reader.GetGuid(1)), Count: reader.GetInt32(2)),
            cancellationToken);
        return rows.ToDictionary(r => r.Key, r => r.Count);
    }

    private NpgsqlParameter Run() => new("run", runId);
}
