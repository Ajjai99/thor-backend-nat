namespace Thor.Workflows.GraphLoad;

/// <summary>
/// Turns a wave index into the chunks that wave writes. Every vertex wave comes before every edge
/// wave and no wave mixes the two: <c>mergeE</c> needs both endpoints to exist, and a wave is the only
/// barrier the state machine has.
/// </summary>
internal static class GraphLoadWavePlanner
{
    public static GraphLoadWave WaveAt(GraphLoadRequest request, GraphLoadPlan plan, int waveIndex, int chunksPerWave)
    {
        var vertexWaves = WavesFor(plan.VertexChunks.Count, chunksPerWave);
        var edgeWaves = WavesFor(plan.EdgeChunks.Count, chunksPerWave);

        if (waveIndex < vertexWaves)
        {
            return Wave(request, waveIndex, plan.VertexChunks, waveIndex, chunksPerWave);
        }

        if (waveIndex < vertexWaves + edgeWaves)
        {
            return Wave(request, waveIndex, plan.EdgeChunks, waveIndex - vertexWaves, chunksPerWave);
        }

        return new GraphLoadWave(waveIndex, [], HasMore: false);
    }

    private static int WavesFor(int chunks, int chunksPerWave) => (chunks + chunksPerWave - 1) / chunksPerWave;

    private static GraphLoadWave Wave(
        GraphLoadRequest request, int waveIndex, IReadOnlyList<GraphLoadChunk> chunks, int phaseWave, int chunksPerWave) =>
        new(
            waveIndex,
            [.. chunks.Skip(phaseWave * chunksPerWave).Take(chunksPerWave)
                .Select(chunk => new GraphLoadChunkRequest(request.TenantId, request.ScanId, chunk.Kind, chunk.Key))],
            HasMore: true);
}
