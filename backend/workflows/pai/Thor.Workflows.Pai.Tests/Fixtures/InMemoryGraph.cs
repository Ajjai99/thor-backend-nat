using Thor.Graph;

namespace Thor.Workflows.Pai.Tests.Fixtures;

/// <summary>
/// An <see cref="IGraphReader"/> over an in-memory graph, with the same semantics as the Gremlin one
/// (which <c>GraphReaderTests</c> checks against a real Gremlin server). Counts label scans so a test
/// can tell a full evaluation from an incremental one.
/// </summary>
internal sealed class InMemoryGraph : IGraphReader
{
    private readonly Dictionary<Guid, (string Label, Dictionary<string, object> Props)> _vertices = [];
    private readonly HashSet<(Guid From, Guid To, string Rel)> _edges = [];

    public int LabelScans { get; private set; }

    public IReadOnlyDictionary<Guid, (string Label, Dictionary<string, object> Props)> Vertices => _vertices;

    public IReadOnlySet<(Guid From, Guid To, string Rel)> Edges => _edges;

    public void AddVertex(Guid id, string label, Dictionary<string, object> props) => _vertices[id] = (label, props);

    public void SetProperty(Guid id, string key, object value) => _vertices[id].Props[key] = value;

    public void RemoveVertex(Guid id)
    {
        _vertices.Remove(id);
        _edges.RemoveWhere(e => e.From == id || e.To == id);
    }

    public bool AddEdge(Guid from, Guid to, string rel) => _edges.Add((from, to, rel));

    public bool RemoveEdge(Guid from, Guid to, string rel) => _edges.Remove((from, to, rel));

    public Task<IReadOnlyList<Guid>> MatchingTargetsAsync(
        Guid tenantId, PrivilegePattern pattern, IReadOnlyCollection<Guid>? candidateIds, CancellationToken cancellationToken = default)
    {
        if (candidateIds is null)
        {
            LabelScans++;
        }

        var candidates = candidateIds ?? (IReadOnlyCollection<Guid>)_vertices.Keys.ToList();
        IReadOnlyList<Guid> matched = [.. candidates.Distinct().Where(id => _vertices.TryGetValue(id, out var v) && v.Label == pattern.TargetLabel && Matches(v.Props, pattern))];
        return Task.FromResult(matched);
    }

    public Task<IReadOnlyList<GraphHop>> InNeighboursAsync(
        Guid tenantId, IReadOnlyCollection<GraphNodeRef> nodes, string edgeType, string neighbourLabel, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<GraphHop>>([
            .. nodes.Distinct().Where(Exists).SelectMany(node => _edges
                .Where(e => e.To == node.Id && e.Rel == edgeType && HasLabel(e.From, neighbourLabel))
                .Select(e => new GraphHop(node.Id, e.From))),
        ]);

    public Task<IReadOnlyList<GraphHop>> OutNeighboursAsync(
        Guid tenantId, IReadOnlyCollection<GraphNodeRef> nodes, string edgeType, string neighbourLabel, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<GraphHop>>([
            .. nodes.Distinct().Where(Exists).SelectMany(node => _edges
                .Where(e => e.From == node.Id && e.Rel == edgeType && HasLabel(e.To, neighbourLabel))
                .Select(e => new GraphHop(node.Id, e.To))),
        ]);

    /// <summary>A vertex id includes its label, so addressing one under the wrong label finds nothing — as in Neptune.</summary>
    private bool Exists(GraphNodeRef node) => HasLabel(node.Id, node.Label);

    private bool HasLabel(Guid id, string label) => _vertices.TryGetValue(id, out var v) && v.Label == label;

    private static bool Matches(Dictionary<string, object> props, PrivilegePattern pattern)
    {
        if (pattern.ConnectorType is { } connectorType && !(props.TryGetValue("connectorType", out var ct) && Equals(ct, connectorType)))
        {
            return false;
        }

        if (!props.TryGetValue(pattern.Field, out var raw) || raw is not string value)
        {
            return false;
        }

        return pattern.Operator switch
        {
            PrivilegeMatchOperator.Equals => value == pattern.Values[0],
            PrivilegeMatchOperator.Contains => value.Contains(pattern.Values[0], StringComparison.Ordinal),
            PrivilegeMatchOperator.StartsWith => value.StartsWith(pattern.Values[0], StringComparison.Ordinal),
            PrivilegeMatchOperator.In => pattern.Values.Contains(value),
            _ => false,
        };
    }
}
