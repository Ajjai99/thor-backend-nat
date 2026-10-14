namespace Thor.Graph;

/// <summary>
/// A vertex addressed the way <see cref="GraphIds.VertexId"/> builds ids — by label as well as id,
/// since the tenant-namespaced vertex id is derived from both.
/// </summary>
public sealed record GraphNodeRef(Guid Id, string Label);

/// <summary>One edge crossed by a one-hop read: the vertex the read started from, and the neighbour it reached.</summary>
public sealed record GraphHop(Guid NodeId, Guid NeighbourId);
