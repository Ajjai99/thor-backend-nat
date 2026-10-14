namespace Thor.Workflows.Pai.Models;

/// <summary>
/// One Distributed Map item: the run's identity and the window of the run's staged group paths
/// (ordered by id) this invocation reads. Built whole by the step that plans the wave rather than by
/// an <c>ItemSelector</c>, because <see cref="ScanId"/> and <see cref="RunId"/> are optional and a
/// reference path that resolves to nothing fails the execution.
/// </summary>
public sealed record PaiChunkRequest(Guid TenantId, Guid? ScanId, Guid? RunId, int Offset, int Limit);

/// <summary>
/// One wave's worth of work. Part of the state machine's contract: the Map's <c>ItemsPath</c> reads
/// <see cref="Chunks"/>, and the Choice that closes the loop reads <see cref="HasMore"/>.
/// </summary>
public sealed record PaiWave(int WaveIndex, IReadOnlyList<PaiChunkRequest> Chunks, bool HasMore);

/// <summary>What <see cref="Steps.PaiNextWaveStep"/> is invoked with: the run's identity, the wave that finished, and the Map's output unchanged.</summary>
public sealed record PaiNextWaveRequest(PaiRequest Request, int WaveIndex, IReadOnlyList<PaiChunkSummary>? Results);

/// <summary>
/// What one fan-out invocation did. <see cref="PathsRead"/> is part of the state machine's contract:
/// the planner sums it to decide whether another wave follows. A tolerated failure reports only
/// <see cref="Failed"/> and <see cref="Offset"/>, which is why <see cref="Failed"/> exists — a zero
/// count otherwise reads as "ran off the end".
/// </summary>
public sealed record PaiChunkSummary(Guid RunId, int Offset, int PathsRead, int RoutesWritten, bool Failed = false);

/// <summary>What expand hands back. The state machine's Choice reads <see cref="IsInProgress"/> straight off the Lambda response.</summary>
public sealed record PaiExpandResult(int LevelsRun, int PathsStaged, bool IsInProgress);
