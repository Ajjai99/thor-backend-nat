using Microsoft.Extensions.Logging;
using Npgsql;
using Thor.DataLayer.Data;
using Thor.Graph;
using Thor.Workflows.Pai.Constants;
using Thor.Workflows.Pai.Persistence;
using Thor.Workflows.Pai.Privilege;

namespace Thor.Workflows.Pai.Seeding;

/// <summary>
/// The change window as PAI needs it, read once for every incremental definition.
/// </summary>
/// <param name="ReachedFromEdges">
/// Per relationship: the containers of the window's changed edges, plus — for a transitive
/// relationship — every group above them. The candidates an incremental definition matches against.
/// </param>
/// <param name="EntitiesByLabel">The window's changed groups and assets, by label.</param>
internal sealed record ChangeSet(
    IReadOnlyDictionary<string, IReadOnlyCollection<GraphNodeRef>> ReachedFromEdges,
    IReadOnlyDictionary<string, IReadOnlyCollection<Guid>> EntitiesByLabel)
{
    public static ChangeSet Empty { get; } = new(
        new Dictionary<string, IReadOnlyCollection<GraphNodeRef>>(), new Dictionary<string, IReadOnlyCollection<Guid>>());

    public static async Task<ChangeSet> ReadAsync(
        TenantDbContext db, IGraphReader graph, Guid tenantId, Guid[] windowScans, IReadOnlyList<RunDefinition> definitions,
        ILogger logger, CancellationToken cancellationToken)
    {
        var reached = new Dictionary<string, IReadOnlyCollection<GraphNodeRef>>();
        foreach (var pattern in definitions.Select(d => d.Pattern).DistinctBy(p => p.EdgeType))
        {
            // Deleted edges included: a revoked membership is exactly a container whose members changed.
            // The container is always the `to` side, because the edge gate normalises direction.
            var containers = await PaiSql.QueryAsync(db, """
                SELECT DISTINCT e.to_id, e.to_type
                FROM tenant.ingest_change_event ce
                JOIN tenant.edge e ON e.id = ce.entity_id
                WHERE ce.scan_id = ANY(@scans) AND ce.entity_type = 'edge' AND e.rel_type = @rel
                """,
                [new NpgsqlParameter("scans", windowScans), new NpgsqlParameter("rel", pattern.EdgeType)],
                reader => new GraphNodeRef(reader.GetGuid(0), reader.GetString(1)),
                cancellationToken);

            reached[pattern.EdgeType] = pattern.IsTransitive
                ? await WalkUpAsync(graph, tenantId, containers, logger, cancellationToken)
                : containers;
        }

        var entities = await PaiSql.QueryAsync(db, """
            SELECT DISTINCT entity_type, entity_id
            FROM tenant.ingest_change_event
            WHERE scan_id = ANY(@scans) AND entity_type IN ('grp', 'asset')
            """,
            [new NpgsqlParameter("scans", windowScans)],
            reader => (Label: reader.GetString(0), Id: reader.GetGuid(1)),
            cancellationToken);

        return new ChangeSet(
            reached,
            entities.GroupBy(e => e.Label).ToDictionary(g => g.Key, g => (IReadOnlyCollection<Guid>)[.. g.Select(e => e.Id)]));
    }

    /// <summary>
    /// Every group reachable up <c>MEMBER_OF</c> from <paramref name="start"/>, start included — one
    /// batched hop per level, a visited set so a cycle or a diamond is expanded once, and the same depth
    /// cap expand walks down with, so the two directions agree on what is reachable.
    /// </summary>
    internal static async Task<IReadOnlyCollection<GraphNodeRef>> WalkUpAsync(
        IGraphReader graph, Guid tenantId, IReadOnlyCollection<GraphNodeRef> start, ILogger logger, CancellationToken cancellationToken)
    {
        var visited = new HashSet<GraphNodeRef>(start);
        IReadOnlyCollection<GraphNodeRef> frontier = start;

        for (var level = 0; level < PaiWaves.DepthCap && frontier.Count > 0; level++)
        {
            var hops = await graph.OutNeighboursAsync(tenantId, frontier, PrivilegePattern.MemberOf, GraphLabels.Group, cancellationToken);
            frontier = [.. hops.Select(hop => new GraphNodeRef(hop.NeighbourId, GraphLabels.Group)).Where(visited.Add)];
        }

        if (frontier.Count > 0)
        {
            logger.LogWarning(
                "Walking up from changed groups reached the depth cap of {Cap} with {Remaining} group(s) still to visit; targets above them are not seeded.",
                PaiWaves.DepthCap, frontier.Count);
        }

        return visited;
    }
}
