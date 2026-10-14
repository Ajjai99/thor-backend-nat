using Gremlin.Net.Driver;
using Gremlin.Net.Driver.Remote;
using Gremlin.Net.Process.Traversal;
using Gremlin.Net.Structure;
using static Gremlin.Net.Process.Traversal.AnonymousTraversalSource;
using static Gremlin.Net.Process.Traversal.__;

namespace Thor.Graph;

/// <summary>
/// Writes vertices as bytecode, not script strings: Neptune does not support script bindings (see
/// <see cref="GraphRelationshipReader"/>).
/// </summary>
public sealed class GraphVertexStore : IGraphVertexStore, IDisposable
{
    private readonly Lazy<GraphTraversalSource> _g;
    private readonly IDisposable? _ownedConnection;

    public GraphVertexStore(NeptuneOptions options)
    {
        var factory = new GremlinConnectionFactory(options);
        _g = new Lazy<GraphTraversalSource>(() => Traversal().With(new DriverRemoteConnection(factory.Client, "g")));
        _ownedConnection = factory;
    }

    /// <summary>Test-only seam — lets tests substitute a fake or local <see cref="IGremlinClient"/> instead of a real Neptune connection.</summary>
    internal GraphVertexStore(IGremlinClient client)
    {
        _g = new Lazy<GraphTraversalSource>(() => Traversal().With(new DriverRemoteConnection(client, "g")));
        _ownedConnection = null;
    }

    public Task UpsertVertexAsync(
        Guid tenantId, string entityType, Guid id, IReadOnlyDictionary<string, object?> properties,
        CancellationToken cancellationToken = default) =>
        UpsertVerticesAsync(tenantId, entityType, [(id, properties)], cancellationToken);

    public async Task UpsertVerticesAsync(
        Guid tenantId, string entityType, IReadOnlyList<(Guid Id, IReadOnlyDictionary<string, object?> Properties)> vertices,
        CancellationToken cancellationToken = default)
    {
        foreach (var (id, properties) in vertices)
        {
            var vid = GraphIds.VertexId(tenantId, entityType, id);

            var vertex = _g.Value.V(vid).Fold()
                .Coalesce<Vertex>(Unfold<Vertex>(), AddV(entityType).Property(T.Id, vid))
                .Property(Cardinality.Single, "tenantId", tenantId.ToString())
                .Property(Cardinality.Single, "pgId", id.ToString());

            foreach (var (key, value) in properties)
            {
                vertex = vertex.Property(Cardinality.Single, key, value ?? "");
            }

            await vertex.Promise(t => t.ToList(), cancellationToken);
        }
    }

    public async Task DeleteVertexAsync(Guid tenantId, string entityType, Guid id, CancellationToken cancellationToken = default)
    {
        await _g.Value.V(GraphIds.VertexId(tenantId, entityType, id)).Drop().Promise(t => t.ToList(), cancellationToken);
    }

    public void Dispose() => _ownedConnection?.Dispose();
}
