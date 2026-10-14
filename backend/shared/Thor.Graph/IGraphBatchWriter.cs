namespace Thor.Graph;

/// <summary>
/// Batched, idempotent upserts into the shared Neptune cluster — the write path for a whole scan's
/// graph, in place of the bulk loader's single cluster-wide queue. Tenant-scoped like every other
/// graph store: the physical id is derived from <c>tenantId</c> inside, so a caller cannot address
/// another tenant's element.
///
/// <para>Every edge's endpoints must already exist: <c>mergeE</c> does not create vertices, and a
/// batch naming a missing one fails as a whole. Callers write a scan's vertices before its edges.</para>
/// </summary>
public interface IGraphBatchWriter
{
    Task<GraphWriteStats> UpsertVerticesAsync(
        Guid tenantId, IReadOnlyList<GraphVertexUpsert> vertices, CancellationToken cancellationToken = default);

    Task<GraphWriteStats> UpsertEdgesAsync(
        Guid tenantId, IReadOnlyList<GraphEdgeUpsert> edges, CancellationToken cancellationToken = default);
}
