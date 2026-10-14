namespace Thor.Graph;

/// <summary>
/// Tenant-namespaced Gremlin vertex id — puts the tenant partition in the vertex's address
/// itself (not just a filterable property), so a caller structurally cannot address a vertex
/// without a tenant id. See ADR §6.1 (single shared, tenant-partitioned Neptune cluster).
/// </summary>
internal static class GraphIds
{
    public static string VertexId(Guid tenantId, string entityType, Guid id) => $"{tenantId:N}:{entityType}:{id:N}";

    /// <summary>
    /// Tenant-namespaced Gremlin edge id, keyed off the canonical Postgres <c>tenant.edge.id</c>.
    /// <see cref="GraphBatchWriter"/> matches on it, so re-writing the same canonical edge row finds
    /// the edge an earlier write created; edges the retired bulk loader wrote carry the same id. The
    /// per-item <see cref="GraphEdgeStore"/> path never sets an edge id explicitly (Neptune assigns
    /// one) and matches on endpoints and label instead.
    /// </summary>
    public static string EdgeId(Guid tenantId, Guid edgeId) => $"{tenantId:N}:edge:{edgeId:N}";
}
