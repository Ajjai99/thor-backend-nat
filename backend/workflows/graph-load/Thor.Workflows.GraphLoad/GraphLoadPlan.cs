namespace Thor.Workflows.GraphLoad;

/// <summary>One file of ids, and which kind of element they are.</summary>
public sealed record GraphLoadChunk(string Kind, string Key);

/// <summary>
/// A scan's whole graph load, decided once by the start-run step: every vertex chunk before every edge
/// chunk, and the manifests it covers, so the deletes at the end are taken over the same manifests as
/// the writes. Kept in S3 rather than in the execution's state, which is capped at 256 KB.
/// </summary>
public sealed record GraphLoadPlan(
    IReadOnlyList<Guid> ScanManifestIds,
    IReadOnlyList<GraphLoadChunk> VertexChunks,
    IReadOnlyList<GraphLoadChunk> EdgeChunks);
