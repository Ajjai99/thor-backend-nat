using Thor.Workflows.Pai.Models;

namespace Thor.Workflows.Pai;

/// <summary>
/// Turns a wave index into the Map items that wave covers, and reads a finished wave's counts to say
/// whether another follows. Pure arithmetic, like <c>AtreWavePlanner</c>: the run never counts its
/// staged paths in advance, and the paths are immutable once expand finishes, so a window always
/// names the same rows.
/// </summary>
public static class PaiWavePlanner
{
    public static PaiWave WaveAt(PaiRequest request, int waveIndex, int pathsPerChunk, int chunksPerWave)
    {
        var firstOffset = waveIndex * chunksPerWave * pathsPerChunk;

        return new PaiWave(
            WaveIndex: waveIndex,
            Chunks: [.. Enumerable.Range(0, chunksPerWave).Select(chunk => new PaiChunkRequest(
                request.TenantId, request.ScanId, request.RunId,
                Offset: firstOffset + (chunk * pathsPerChunk),
                Limit: pathsPerChunk))],
            HasMore: true);
    }

    /// <summary>
    /// A full wave may be followed by more; a short one ran off the end. A failed chunk counts as full,
    /// because its zero would otherwise end the run early and skip every later window — but a wave
    /// where every chunk failed ends the loop, or a run that can no longer read anything would loop
    /// until the execution-history cap. The lost windows are already recorded either way.
    /// </summary>
    public static PaiWave NextWaveAfter(
        PaiRequest request, int finishedWaveIndex, IReadOnlyList<PaiChunkSummary> results, int pathsPerChunk, int chunksPerWave)
    {
        var pathsRead = results.Sum(r => r.Failed ? pathsPerChunk : r.PathsRead);
        var waveWasFull = pathsRead == chunksPerWave * pathsPerChunk;
        var anyChunkRan = results.Any(r => !r.Failed);

        return waveWasFull && anyChunkRan
            ? WaveAt(request, finishedWaveIndex + 1, pathsPerChunk, chunksPerWave)
            : new PaiWave(finishedWaveIndex + 1, Chunks: [], HasMore: false);
    }
}
