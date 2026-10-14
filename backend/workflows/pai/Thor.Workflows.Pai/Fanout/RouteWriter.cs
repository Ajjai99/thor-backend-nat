using Microsoft.Extensions.Logging;
using Npgsql;
using Thor.DataLayer.Data;
using Thor.Graph;
using Thor.Workflows.Pai.Constants;
using Thor.Workflows.Pai.Persistence;
using Thor.Workflows.Pai.Privilege;

namespace Thor.Workflows.Pai.Fanout;

internal sealed record FanoutProgress(int PathsRead, int RoutesWritten);

/// <summary>
/// One fan-out chunk: reads a window of the run's staged group paths, fetches the direct account
/// members of each path's last node (one batched hop per relationship), and upserts a route per
/// (account, path).
///
/// <para>Rows are upserted, never deleted first — readers never see a target with no routes while a
/// run is in flight. A re-proven route gets this run's id and a fresh <c>computed_at</c>; finalize
/// removes the ones that were not.</para>
/// </summary>
internal sealed class RouteWriter(TenantDbContext readContext, TenantDbContext writeContext, IGraphReader graph, Guid tenantId, Guid runId, ILogger logger)
{
    /// <summary>Rows per upsert — keeps the unnest arrays small while amortising the round trip.</summary>
    private const int BatchSize = 2000;

    private sealed record StagedPath(long Id, Guid DefinitionId, Guid TargetId, int Depth, Guid[] NodeIds);

    private sealed record Route(Guid AccountId, Guid TargetId, string TargetType, Guid DefinitionId, string Json, string Hash, Guid[] Via);

    public async Task<FanoutProgress> WriteAsync(
        IReadOnlyDictionary<Guid, RunDefinition> definitions, int offset, int limit, CancellationToken cancellationToken)
    {
        var paths = await PaiSql.QueryAsync(readContext, """
            SELECT id, definition_id, target_id, depth, node_ids
            FROM tenant.pai_group_path
            WHERE run_id = @run
            ORDER BY id
            OFFSET @offset LIMIT @limit
            """,
            [new NpgsqlParameter("run", runId), new NpgsqlParameter("offset", offset), new NpgsqlParameter("limit", limit)],
            reader => new StagedPath(reader.GetInt64(0), reader.GetGuid(1), reader.GetGuid(2), reader.GetInt32(3), reader.GetFieldValue<Guid[]>(4)),
            cancellationToken);

        if (paths.Count == 0)
        {
            return new FanoutProgress(0, 0);
        }

        var routes = new Dictionary<(Guid, Guid, Guid, string), Route>();
        foreach (var byEdge in paths.GroupBy(p => definitions[p.DefinitionId].Pattern.EdgeType))
        {
            var lastNodes = byEdge.Select(p => LastNode(p, definitions[p.DefinitionId].Pattern)).ToHashSet();
            var members = (await graph.InNeighboursAsync(tenantId, lastNodes, byEdge.Key, GraphLabels.Account, cancellationToken))
                .ToLookup(hop => hop.NodeId, hop => hop.NeighbourId);

            foreach (var path in byEdge)
            {
                var pattern = definitions[path.DefinitionId].Pattern;
                foreach (var accountId in members[path.NodeIds[^1]].Distinct())
                {
                    var (json, hash) = AccessPath.Serialize(accountId, path.NodeIds, pattern);
                    routes.TryAdd(
                        (accountId, path.TargetId, path.DefinitionId, hash),
                        new Route(accountId, path.TargetId, pattern.TargetLabel, path.DefinitionId, json, hash, path.NodeIds));
                }
            }
        }

        var written = 0;
        foreach (var batch in routes.Values.Chunk(BatchSize))
        {
            written += await UpsertAsync(batch, cancellationToken);
        }

        logger.LogInformation(
            "PAI run {RunId} chunk [{Offset}, {End}): {Paths} path(s), {Routes} route(s) written.",
            runId, offset, offset + limit, paths.Count, written);
        return new FanoutProgress(paths.Count, written);
    }

    private static GraphNodeRef LastNode(StagedPath path, PrivilegePattern pattern) =>
        new(path.NodeIds[^1], path.Depth == 0 ? pattern.TargetLabel : GraphLabels.Group);

    /// <summary>
    /// <c>unnest</c> keeps the statement and its parameter count fixed however large the batch. The
    /// account must still be live in Postgres: the graph can briefly hold an account whose row is gone,
    /// and a foreign-key violation would fail the whole chunk.
    /// </summary>
    private Task<int> UpsertAsync(IReadOnlyList<Route> batch, CancellationToken cancellationToken) =>
        PaiSql.ExecAsync(writeContext, """
            INSERT INTO tenant.pai_result
                (id, account_id, target_id, target_type, access_path, access_path_hash, via_node_ids,
                 privilege_definition_id, is_privileged, is_active, run_id, computed_at)
            SELECT gen_random_uuid(), r.account_id, r.target_id, r.target_type, r.access_path, r.access_path_hash,
                   string_to_array(r.via, ',')::uuid[], r.definition_id, TRUE, TRUE, @run, now()
            FROM unnest(@account_ids, @target_ids, @target_types, @definition_ids, @paths, @hashes, @vias)
                 AS r(account_id, target_id, target_type, definition_id, access_path, access_path_hash, via)
            WHERE EXISTS (SELECT 1 FROM tenant.account a WHERE a.id = r.account_id AND NOT a.is_deleted)
            ON CONFLICT (account_id, target_type, target_id, privilege_definition_id, access_path_hash)
            DO UPDATE SET is_active = TRUE, run_id = excluded.run_id, computed_at = excluded.computed_at, via_node_ids = excluded.via_node_ids
            """,
            [
                new NpgsqlParameter("run", runId),
                new NpgsqlParameter("account_ids", batch.Select(r => r.AccountId).ToArray()),
                new NpgsqlParameter("target_ids", batch.Select(r => r.TargetId).ToArray()),
                new NpgsqlParameter("target_types", batch.Select(r => r.TargetType).ToArray()),
                new NpgsqlParameter("definition_ids", batch.Select(r => r.DefinitionId).ToArray()),
                new NpgsqlParameter("paths", batch.Select(r => r.Json).ToArray()),
                new NpgsqlParameter("hashes", batch.Select(r => r.Hash).ToArray()),
                new NpgsqlParameter("vias", batch.Select(r => string.Join(',', r.Via)).ToArray()),
            ],
            cancellationToken);
}
