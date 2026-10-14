namespace Thor.Graph;

/// <summary>
/// Read side of the graph, for PAI. Every call is bounded — a label match or a single hop — so no
/// query can grow into the variable-length expansion that runs into Neptune's query timeout; callers
/// walk further by calling again, level by level. Each method takes an explicit tenant id and seeds
/// through <see cref="TenantTraversal"/>. Results carry Postgres ids (the <c>pgId</c> both write
/// paths mirror onto every vertex).
/// </summary>
public interface IGraphReader
{
    /// <summary>
    /// The targets matching <paramref name="pattern"/>: every one in the tenant when
    /// <paramref name="candidateIds"/> is null, otherwise those of the candidates that match.
    /// </summary>
    Task<IReadOnlyList<Guid>> MatchingTargetsAsync(
        Guid tenantId, PrivilegePattern pattern, IReadOnlyCollection<Guid>? candidateIds, CancellationToken cancellationToken = default);

    /// <summary>
    /// For each of <paramref name="nodes"/>, the <paramref name="neighbourLabel"/> vertices with an
    /// <paramref name="edgeType"/> edge pointing <em>at</em> it — a container's members.
    /// </summary>
    Task<IReadOnlyList<GraphHop>> InNeighboursAsync(
        Guid tenantId, IReadOnlyCollection<GraphNodeRef> nodes, string edgeType, string neighbourLabel, CancellationToken cancellationToken = default);

    /// <summary>
    /// For each of <paramref name="nodes"/>, the <paramref name="neighbourLabel"/> vertices its
    /// <paramref name="edgeType"/> edges point <em>to</em> — a member's containers.
    /// </summary>
    Task<IReadOnlyList<GraphHop>> OutNeighboursAsync(
        Guid tenantId, IReadOnlyCollection<GraphNodeRef> nodes, string edgeType, string neighbourLabel, CancellationToken cancellationToken = default);
}
