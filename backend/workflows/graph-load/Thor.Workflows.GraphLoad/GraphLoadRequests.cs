namespace Thor.Workflows.GraphLoad;

/// <summary>
/// One scan's graph load, as the scan-completion gate starts it. <see cref="TenantId"/> is trusted as
/// verified upstream — the gate resolved its routing before starting the run, and every tenant
/// database connection validates it again.
/// </summary>
public sealed record GraphLoadRequest(Guid TenantId, Guid ScanId);

/// <summary>
/// One Map item: a file of ids of one <see cref="Kind"/> to read from Postgres and write to Neptune.
/// Built whole by the step that plans the wave, so the state machine needs no ItemSelector — see
/// <c>AtreChunkRequest</c> for why that matters. Ids rather than rows because a Map item is capped at
/// 256 KB, and rather than an offset because the write set is a join that is cheap to run once and
/// expensive to re-run per chunk.
/// </summary>
public sealed record GraphLoadChunkRequest(Guid TenantId, Guid ScanId, string Kind, string Key);

/// <summary>
/// One wave of chunks. Part of the state machine's contract: the Map reads <see cref="Chunks"/> and the
/// loop's Choice reads <see cref="HasMore"/>, which is true while <see cref="Chunks"/> has work in it.
/// </summary>
public sealed record GraphLoadWave(int WaveIndex, IReadOnlyList<GraphLoadChunkRequest> Chunks, bool HasMore);

public sealed record GraphLoadNextWaveRequest(GraphLoadRequest Request, int WaveIndex);

/// <summary>What one chunk wrote — for the execution history and logs; nothing downstream reads it.</summary>
public sealed record GraphLoadChunkResult(string Key, string Kind, int Elements, int Requests, int Retries);

/// <summary>What Step Functions writes to <c>$.Error</c> when a Catch fires; both halves are optional.</summary>
public sealed record GraphLoadFailureCause(string? Error, string? Cause);

public sealed record GraphLoadFailureRequest(GraphLoadRequest Request, GraphLoadFailureCause? Error);

/// <summary>
/// Values of <see cref="GraphLoadChunkRequest.Kind"/>: the three vertex labels the graph holds, and
/// edges. Vertex labels are the <c>entity_type</c> strings ingestion records, so they double as the
/// <c>ingest_change_event</c> filter and the <c>GraphIds</c> label.
/// </summary>
public static class GraphLoadKinds
{
    public const string Account = "account";

    public const string Group = "grp";

    public const string Asset = "asset";

    public const string Edge = "edge";

    public static readonly IReadOnlyList<string> Vertices = [Account, Group, Asset];
}
