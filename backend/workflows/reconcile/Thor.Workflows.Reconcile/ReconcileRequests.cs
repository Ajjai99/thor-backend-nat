namespace Thor.Workflows.Reconcile;

/// <summary>
/// One scan's reconcile run, as the scan-completion gate starts it. <see cref="TenantId"/> is trusted as
/// verified upstream — the gate resolved its routing — and every tenant database connection validates it
/// again. <see cref="ScanId"/> is required: with no scan there is no ledger, and "everything is absent"
/// is the only conclusion left.
/// </summary>
public sealed record ReconcileRequest(Guid TenantId, Guid ScanId);

/// <summary>
/// One Map item: the run's staged deletions with <c>seq</c> in <c>(FromSeq, ToSeq]</c>. Built whole by the
/// step that plans the wave, so the state machine needs no ItemSelector.
/// </summary>
public sealed record ReconcileChunkRequest(Guid TenantId, Guid ScanId, long FromSeq, long ToSeq);

/// <summary>
/// One wave of chunks. Part of the state machine's contract: the Map reads <see cref="Chunks"/>, the
/// loop's Choice reads <see cref="HasMore"/>, and next-wave is handed <see cref="Total"/> back so it
/// needs no database.
/// </summary>
public sealed record ReconcileWave(int WaveIndex, long Total, IReadOnlyList<ReconcileChunkRequest> Chunks, bool HasMore);

public sealed record ReconcileNextWaveRequest(ReconcileRequest Request, int WaveIndex, long Total);

/// <summary>What one chunk deleted — for the execution history and logs; nothing downstream reads it.</summary>
public sealed record ReconcileChunkResult(long FromSeq, long ToSeq, int EntitiesDeleted, int EdgesDeleted);

/// <summary>What Step Functions writes to <c>$.Error</c> when a Catch fires; both halves are optional.</summary>
public sealed record ReconcileFailureCause(string? Error, string? Cause);

public sealed record ReconcileFailureRequest(ReconcileRequest Request, ReconcileFailureCause? Error);
