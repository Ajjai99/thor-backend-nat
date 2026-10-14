using Thor.Workflows.Pai.Models;
using Xunit;

namespace Thor.Workflows.Pai.Tests;

public sealed class PaiWavePlannerTests
{
    private static readonly PaiRequest Request = new(Guid.NewGuid(), Guid.NewGuid());

    [Fact]
    public void WaveAt_LaysOutContiguousWindows()
    {
        var wave = PaiWavePlanner.WaveAt(Request, waveIndex: 2, pathsPerChunk: 10, chunksPerWave: 3);

        Assert.Equal([60, 70, 80], wave.Chunks.Select(c => c.Offset));
        Assert.All(wave.Chunks, c => Assert.Equal(10, c.Limit));
        Assert.True(wave.HasMore);
    }

    [Fact]
    public void AFullWave_IsFollowedByTheNext()
    {
        var next = PaiWavePlanner.NextWaveAfter(Request, 0, [Chunk(10), Chunk(10)], pathsPerChunk: 10, chunksPerWave: 2);

        Assert.True(next.HasMore);
        Assert.Equal(1, next.WaveIndex);
    }

    [Fact]
    public void AShortWave_EndsTheLoop()
    {
        var next = PaiWavePlanner.NextWaveAfter(Request, 0, [Chunk(10), Chunk(4)], pathsPerChunk: 10, chunksPerWave: 2);

        Assert.False(next.HasMore);
        Assert.Empty(next.Chunks);
    }

    [Fact]
    public void AFailedChunk_CountsAsFull_SoLaterWindowsAreNotSkipped()
    {
        var next = PaiWavePlanner.NextWaveAfter(Request, 0, [Chunk(10), Failed()], pathsPerChunk: 10, chunksPerWave: 2);

        Assert.True(next.HasMore);
    }

    [Fact]
    public void AWaveWhereEveryChunkFailed_EndsTheLoop()
    {
        var next = PaiWavePlanner.NextWaveAfter(Request, 0, [Failed(), Failed()], pathsPerChunk: 10, chunksPerWave: 2);

        Assert.False(next.HasMore);
    }

    private static PaiChunkSummary Chunk(int paths) => new(Guid.Empty, 0, paths, 0);

    private static PaiChunkSummary Failed() => new(Guid.Empty, 0, 0, 0, Failed: true);
}
