namespace Thor.Graph;

/// <summary>
/// One vertex for <see cref="IGraphBatchWriter.UpsertVerticesAsync"/>. <see cref="Properties"/> values
/// must be plain CLR primitives (string, bool, int, long, double); a <c>null</c> value is skipped
/// rather than written, so it never clears what the graph already holds.
/// </summary>
public sealed record GraphVertexUpsert(string EntityType, Guid Id, IReadOnlyDictionary<string, object?> Properties);

/// <summary>
/// One edge for <see cref="IGraphBatchWriter.UpsertEdgesAsync"/>, keyed by the canonical Postgres
/// <c>tenant.edge.id</c> so a retry finds the edge it already wrote instead of adding a second one.
/// </summary>
public sealed record GraphEdgeUpsert(
    Guid EdgeId, string RelType, GraphVertexRef From, GraphVertexRef To, IReadOnlyDictionary<string, object?> Properties);

/// <summary>What one <see cref="IGraphBatchWriter"/> call did — for logs, not for control flow.</summary>
public sealed record GraphWriteStats(int Elements, int Requests, int Retries);
