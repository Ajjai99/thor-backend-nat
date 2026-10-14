using Gremlin.Net.Process.Traversal;
using Gremlin.Net.Structure;

namespace Thor.Graph;

/// <summary>
/// The only place a read traversal is seeded, so that every one of them is tenant-scoped.
///
/// <para>Writes get isolation structurally — <see cref="GraphIds.VertexId"/> puts the tenant in the
/// vertex address. Reads cannot rely on that, because a privilege pattern seeds from a label scan
/// ("every <c>grp</c> named Domain Admins"), which would otherwise cross every tenant in the shared
/// cluster. <see cref="GraphBatchWriter"/> stamps <c>tenantId</c> on every vertex, so the filter
/// exists; this type guarantees it is applied, id seeds included.</para>
/// </summary>
internal static class TenantTraversal
{
    public const string TenantProperty = "tenantId";

    /// <summary>Seeds from a label scan — the caller knows what a target looks like but not which vertices are targets.</summary>
    public static GraphTraversal<Vertex, Vertex> SeedByLabel(GraphTraversalSource g, Guid tenantId, string label) =>
        g.V().HasLabel(label).Has(TenantProperty, tenantId.ToString());

    /// <summary>
    /// Seeds from known vertices of one label, addressed by their tenant-namespaced ids. The ids already
    /// carry the tenant; the property filter is applied anyway, because an invariant with an exception
    /// is not one.
    /// </summary>
    public static GraphTraversal<Vertex, Vertex> SeedByIds(GraphTraversalSource g, Guid tenantId, string label, IEnumerable<Guid> ids) =>
        g.V(ids.Select(id => (object)GraphIds.VertexId(tenantId, label, id)).ToArray())
            .HasLabel(label)
            .Has(TenantProperty, tenantId.ToString());
}
