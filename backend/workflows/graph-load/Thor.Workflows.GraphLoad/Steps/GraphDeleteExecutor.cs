using Thor.Graph;

namespace Thor.Workflows.GraphLoad.Steps;

/// <summary>Executes vertex/edge deletes directly against Neptune, for <see cref="GraphLoadFinalizeStep"/>.</summary>
internal static class GraphDeleteExecutor
{
    private const int MaxDegreeOfParallelism = 4;

    public static Task DeleteVerticesAsync(
        IGraphVertexStore vertexStore, Guid tenantId, IReadOnlyList<(string Kind, Guid Id)> toDelete, CancellationToken cancellationToken) =>
        Parallel.ForEachAsync(toDelete, new ParallelOptions { MaxDegreeOfParallelism = MaxDegreeOfParallelism, CancellationToken = cancellationToken },
            async (item, ct) => await vertexStore.DeleteVertexAsync(tenantId, item.Kind, item.Id, ct));

    public static Task DeleteEdgesAsync(
        IGraphEdgeStore edgeStore, Guid tenantId, IReadOnlyList<(string RelType, GraphVertexRef From, GraphVertexRef To)> toDelete,
        CancellationToken cancellationToken) =>
        Parallel.ForEachAsync(toDelete, new ParallelOptions { MaxDegreeOfParallelism = MaxDegreeOfParallelism, CancellationToken = cancellationToken },
            async (item, ct) => await edgeStore.DeleteEdgeAsync(tenantId, item.RelType, item.From, item.To, ct));
}
