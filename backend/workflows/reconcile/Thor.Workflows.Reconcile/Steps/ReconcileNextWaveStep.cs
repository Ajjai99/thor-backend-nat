using Thor.Workflows.Abstractions;

namespace Thor.Workflows.Reconcile.Steps;

/// <summary>
/// The bottom of the loop: hands back the next wave, or an empty one once every window is done. Touches no
/// database — start-run handed the staged total along with wave zero. A step rather than a <c>Pass</c>
/// state because Amazon States Language cannot do this arithmetic.
/// </summary>
public sealed class ReconcileNextWaveStep : WorkflowStep<ReconcileNextWaveRequest>
{
    private readonly int _rowsPerChunk;
    private readonly int _chunksPerWave;

    /// <summary>Test seam — lets tests pin the two dials the parameterless constructor reads from the environment.</summary>
    public ReconcileNextWaveStep(int rowsPerChunk, int chunksPerWave)
    {
        _rowsPerChunk = rowsPerChunk;
        _chunksPerWave = chunksPerWave;
    }

    public ReconcileNextWaveStep()
        : this(ReconcileSettings.RowsPerChunk(), ReconcileSettings.ChunksPerWave())
    {
    }

    protected override Task<StepResult> ExecuteAsync(ReconcileNextWaveRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(StepResult.Completed(
            ReconcileWavePlanner.WaveAt(request.Request, request.Total, request.WaveIndex + 1, _rowsPerChunk, _chunksPerWave)));
}
