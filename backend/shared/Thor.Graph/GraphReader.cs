using Gremlin.Net.Driver;
using Gremlin.Net.Driver.Remote;
using Gremlin.Net.Process.Traversal;
using Gremlin.Net.Structure;
using static Gremlin.Net.Process.Traversal.AnonymousTraversalSource;
using GremlinPath = Gremlin.Net.Structure.Path;

namespace Thor.Graph;

/// <summary>
/// PAI's reads. Bytecode, not script strings, for <see cref="GraphRelationshipReader"/>'s reason:
/// Neptune does not support script bindings, and bytecode keeps every caller-derived value a typed
/// argument.
/// </summary>
public sealed class GraphReader : IGraphReader, IDisposable
{
    /// <summary>
    /// Seed vertices per query. Neptune materialises <c>g.V(ids)</c> eagerly and caps request size,
    /// so a large seed is split rather than sent as one list.
    /// </summary>
    internal const int SeedBatchSize = 200;

    /// <summary>The Postgres id <see cref="GraphBatchWriter"/> mirrors onto every vertex — how results map back.</summary>
    private const string PostgresIdProperty = "pgId";

    private readonly Lazy<GraphTraversalSource> _g;
    private readonly IDisposable? _ownedConnection;

    public GraphReader(NeptuneOptions options)
    {
        // Lazy so constructing the reader never opens a connection; the first read does.
        var factory = new GremlinConnectionFactory(options);
        _g = new Lazy<GraphTraversalSource>(() => Traversal().With(new DriverRemoteConnection(factory.Client, "g")));
        _ownedConnection = factory;
    }

    /// <summary>Test-only seam — lets tests substitute a Gremlin client of their own instead of a real Neptune connection.</summary>
    internal GraphReader(IGremlinClient client)
    {
        _g = new Lazy<GraphTraversalSource>(() => Traversal().With(new DriverRemoteConnection(client, "g")));
        _ownedConnection = null;
    }

    public async Task<IReadOnlyList<Guid>> MatchingTargetsAsync(
        Guid tenantId, PrivilegePattern pattern, IReadOnlyCollection<Guid>? candidateIds, CancellationToken cancellationToken = default)
    {
        IEnumerable<Guid[]?> seeds = candidateIds is null ? [null] : candidateIds.Distinct().Chunk(SeedBatchSize);
        var matched = new List<Guid>();

        foreach (var batch in seeds)
        {
            var seed = batch is null
                ? TenantTraversal.SeedByLabel(_g.Value, tenantId, pattern.TargetLabel)
                : TenantTraversal.SeedByIds(_g.Value, tenantId, pattern.TargetLabel, batch);

            var ids = await Match(seed, pattern).Values<string>(PostgresIdProperty).Promise(t => t.ToList(), cancellationToken);
            foreach (var id in ids)
            {
                if (Guid.TryParse(id, out var parsed))
                {
                    matched.Add(parsed);
                }
            }
        }

        return matched;
    }

    public Task<IReadOnlyList<GraphHop>> InNeighboursAsync(
        Guid tenantId, IReadOnlyCollection<GraphNodeRef> nodes, string edgeType, string neighbourLabel, CancellationToken cancellationToken = default) =>
        HopAsync(tenantId, nodes, inbound: true, edgeType, neighbourLabel, cancellationToken);

    public Task<IReadOnlyList<GraphHop>> OutNeighboursAsync(
        Guid tenantId, IReadOnlyCollection<GraphNodeRef> nodes, string edgeType, string neighbourLabel, CancellationToken cancellationToken = default) =>
        HopAsync(tenantId, nodes, inbound: false, edgeType, neighbourLabel, cancellationToken);

    /// <summary>
    /// One hop from every node, batched per label: a vertex id is built from the tenant <em>and</em> the
    /// label, so a mixed-label seed cannot share one <c>g.V(...)</c>. Each result is the two-element
    /// path <c>[node, neighbour]</c> by <c>pgId</c>.
    /// </summary>
    private async Task<IReadOnlyList<GraphHop>> HopAsync(
        Guid tenantId, IReadOnlyCollection<GraphNodeRef> nodes, bool inbound, string edgeType, string neighbourLabel,
        CancellationToken cancellationToken)
    {
        var hops = new List<GraphHop>();

        foreach (var byLabel in nodes.GroupBy(node => node.Label, StringComparer.Ordinal))
        {
            foreach (var batch in byLabel.Select(node => node.Id).Distinct().Chunk(SeedBatchSize))
            {
                var seed = TenantTraversal.SeedByIds(_g.Value, tenantId, byLabel.Key, batch);
                var neighbours = inbound ? seed.In(edgeType) : seed.Out(edgeType);

                var paths = await neighbours
                    .HasLabel(neighbourLabel)
                    .Has(TenantTraversal.TenantProperty, tenantId.ToString())
                    .Path().By(PostgresIdProperty)
                    .Promise(t => t.ToList(), cancellationToken);

                foreach (var path in paths.OfType<GremlinPath>())
                {
                    if (path.Objects.Count == 2
                        && Guid.TryParse(path.Objects[0] as string, out var nodeId)
                        && Guid.TryParse(path.Objects[1] as string, out var neighbourId))
                    {
                        hops.Add(new GraphHop(nodeId, neighbourId));
                    }
                }
            }
        }

        return hops;
    }

    /// <summary>Appends the pattern's predicates: the connector, then the one field it matches on.</summary>
    private static GraphTraversal<Vertex, Vertex> Match(GraphTraversal<Vertex, Vertex> vertices, PrivilegePattern pattern)
    {
        if (pattern.ConnectorType is { } connectorType)
        {
            // Int16, the type graph-load writes it as (the entities' connector_type is a smallint).
            vertices = vertices.Has("connectorType", (short)connectorType);
        }

        return pattern.Operator switch
        {
            PrivilegeMatchOperator.Equals => vertices.Has(pattern.Field, pattern.Values[0]),
            PrivilegeMatchOperator.Contains => vertices.Has(pattern.Field, TextP.Containing(pattern.Values[0])),
            PrivilegeMatchOperator.StartsWith => vertices.Has(pattern.Field, TextP.StartingWith(pattern.Values[0])),
            PrivilegeMatchOperator.In => vertices.Has(pattern.Field, P.Within(pattern.Values.Cast<object>().ToArray())),
            _ => throw new ArgumentOutOfRangeException(nameof(pattern), pattern.Operator, "Unsupported privilege match operator."),
        };
    }

    public void Dispose() => _ownedConnection?.Dispose();
}
