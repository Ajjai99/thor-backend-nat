using Gremlin.Net.Driver;
using Gremlin.Net.Driver.Remote;
using Gremlin.Net.Process.Traversal;
using Gremlin.Net.Structure;
using static Gremlin.Net.Process.Traversal.AnonymousTraversalSource;
using static Gremlin.Net.Process.Traversal.__;

namespace Thor.Graph;

/// <summary>
/// Writes edges as bytecode, not script strings: Neptune does not support script bindings (see
/// <see cref="GraphRelationshipReader"/>).
/// </summary>
public sealed class GraphEdgeStore : IGraphEdgeStore, IDisposable
{
    private readonly Lazy<GraphTraversalSource> _g;
    private readonly IDisposable? _ownedConnection;

    public GraphEdgeStore(NeptuneOptions options)
    {
        var factory = new GremlinConnectionFactory(options);
        _g = new Lazy<GraphTraversalSource>(() => Traversal().With(new DriverRemoteConnection(factory.Client, "g")));
        _ownedConnection = factory;
    }

    /// <summary>Test-only seam — lets tests substitute a fake or local <see cref="IGremlinClient"/> instead of a real Neptune connection.</summary>
    internal GraphEdgeStore(IGremlinClient client)
    {
        _g = new Lazy<GraphTraversalSource>(() => Traversal().With(new DriverRemoteConnection(client, "g")));
        _ownedConnection = null;
    }

    public async Task UpsertEdgeAsync(
        Guid tenantId, string relType, GraphVertexRef from, GraphVertexRef to,
        IReadOnlyDictionary<string, object?> properties, CancellationToken cancellationToken = default)
    {
        var edge = _g.Value
            .V(GraphIds.VertexId(tenantId, from.EntityType, from.Id)).As("a")
            .V(GraphIds.VertexId(tenantId, to.EntityType, to.Id)).As("b")
            .Coalesce<Edge>(
                InE(relType).Where(OutV().As("a")).Where(InV().As("b")),
                AddE(relType).From("a").To("b"))
            .Property("tenantId", tenantId.ToString());

        // Edge properties take no cardinality, unlike vertex properties; setting a key again replaces it.
        foreach (var (key, value) in properties)
        {
            edge = edge.Property(key, value ?? "");
        }

        await edge.Promise(t => t.ToList(), cancellationToken);
    }

    public async Task DeleteEdgeAsync(
        Guid tenantId, string relType, GraphVertexRef from, GraphVertexRef to, CancellationToken cancellationToken = default)
    {
        await _g.Value
            .V(GraphIds.VertexId(tenantId, from.EntityType, from.Id))
            .OutE(relType).Where(InV().HasId(GraphIds.VertexId(tenantId, to.EntityType, to.Id)))
            .Drop()
            .Promise(t => t.ToList(), cancellationToken);
    }

    public void Dispose() => _ownedConnection?.Dispose();
}
