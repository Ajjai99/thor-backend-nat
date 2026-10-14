namespace Thor.Workflows.Reconcile;

/// <summary>
/// Turns a wave index into the windows over the run's staged deletions that wave covers. Pure arithmetic:
/// start-run knows exactly how many it staged, and the windows are over a contiguous <c>seq</c>, so no
/// wave needs to discover where the end is.
/// </summary>
internal static class ReconcileWavePlanner
{
    public static ReconcileWave WaveAt(ReconcileRequest request, long total, int waveIndex, int rowsPerChunk, int chunksPerWave)
    {
        var chunks = new List<ReconcileChunkRequest>(chunksPerWave);
        for (var chunk = 0; chunk < chunksPerWave; chunk++)
        {
            var from = ((long)waveIndex * chunksPerWave + chunk) * rowsPerChunk;
            if (from >= total)
            {
                break;
            }

            chunks.Add(new ReconcileChunkRequest(request.TenantId, request.ScanId, from, Math.Min(from + rowsPerChunk, total)));
        }

        return new ReconcileWave(waveIndex, total, chunks, HasMore: chunks.Count > 0);
    }
}
