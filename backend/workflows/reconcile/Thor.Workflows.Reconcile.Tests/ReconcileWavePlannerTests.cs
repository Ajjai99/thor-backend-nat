using Xunit;

namespace Thor.Workflows.Reconcile.Tests;

public sealed class ReconcileWavePlannerTests
{
    private static readonly ReconcileRequest Request = new(Guid.NewGuid(), Guid.NewGuid());

    [Fact]
    public void WavesCoverEveryStagedRowOnce_AndTheLastWindowIsShort()
    {
        var windows = new List<(long From, long To)>();
        var wave = ReconcileWavePlanner.WaveAt(Request, total: 11, waveIndex: 0, rowsPerChunk: 3, chunksPerWave: 2);
        while (wave.HasMore)
        {
            windows.AddRange(wave.Chunks.Select(c => (c.FromSeq, c.ToSeq)));
            wave = ReconcileWavePlanner.WaveAt(Request, wave.Total, wave.WaveIndex + 1, 3, 2);
        }

        Assert.Equal([(0L, 3L), (3L, 6L), (6L, 9L), (9L, 11L)], windows);
    }

    [Fact]
    public void NothingStaged_IsAnEmptyWave() =>
        Assert.False(ReconcileWavePlanner.WaveAt(Request, total: 0, waveIndex: 0, rowsPerChunk: 3, chunksPerWave: 2).HasMore);
}
