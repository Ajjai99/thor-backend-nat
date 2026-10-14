using Thor.Workflows.Abstractions;
using Thor.Workflows.Pai.Models;

namespace Thor.Workflows.Pai.Steps;

/// <summary>
/// Reads the counts the wave returned and builds the next wave, or ends the loop. Touches no database;
/// a step rather than a <c>Pass</c> state because Amazon States Language cannot sum an array.
/// </summary>
public sealed class PaiNextWaveStep : WorkflowStep<PaiNextWaveRequest>
{
    private readonly int _pathsPerChunk;
    private readonly int _chunksPerWave;

    /// <summary>Test seam — lets tests pin the two dials the parameterless constructor reads from the environment.</summary>
    public PaiNextWaveStep(int pathsPerChunk, int chunksPerWave)
    {
        _pathsPerChunk = pathsPerChunk;
        _chunksPerWave = chunksPerWave;
    }

    public PaiNextWaveStep()
        : this(PaiWaves.PathsPerChunkFromEnvironment(), PaiWaves.ChunksPerWaveFromEnvironment())
    {
    }

    protected override Task<StepResult> ExecuteAsync(PaiNextWaveRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(StepResult.Completed(PaiWavePlanner.NextWaveAfter(
            request.Request, request.WaveIndex, request.Results ?? [], _pathsPerChunk, _chunksPerWave)));
}
