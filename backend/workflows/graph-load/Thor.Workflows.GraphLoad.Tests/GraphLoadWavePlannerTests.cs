using System.Text.Json;
using Thor.Workflows.GraphLoad.Steps;
using Xunit;

namespace Thor.Workflows.GraphLoad.Tests;

/// <summary>Covers how a plan becomes waves, and <see cref="GraphLoadNextWaveStep"/>, which reads the plan to step through them.</summary>
public sealed class GraphLoadWavePlannerTests
{
    private static readonly GraphLoadRequest Request = new(Guid.NewGuid(), Guid.NewGuid());

    private static GraphLoadPlan Plan(int vertexChunks, int edgeChunks) =>
        new(
            [],
            [.. Enumerable.Range(0, vertexChunks).Select(i => new GraphLoadChunk(GraphLoadKinds.Account, $"v{i}"))],
            [.. Enumerable.Range(0, edgeChunks).Select(i => new GraphLoadChunk(GraphLoadKinds.Edge, $"e{i}"))]);

    [Fact]
    public void WaveAt_EveryVertexWaveComesBeforeAnyEdgeWave_AndNoWaveMixesThem()
    {
        var plan = Plan(vertexChunks: 3, edgeChunks: 3);

        var waves = Enumerable.Range(0, 5).Select(i => GraphLoadWavePlanner.WaveAt(Request, plan, i, chunksPerWave: 2)).ToList();

        Assert.Equal(["v0", "v1"], waves[0].Chunks.Select(c => c.Key));
        Assert.Equal(["v2"], waves[1].Chunks.Select(c => c.Key)); // a short vertex wave, not topped up with edges
        Assert.Equal(["e0", "e1"], waves[2].Chunks.Select(c => c.Key));
        Assert.Equal(["e2"], waves[3].Chunks.Select(c => c.Key));
        Assert.All(waves.Take(2).SelectMany(w => w.Chunks), c => Assert.Equal(GraphLoadKinds.Account, c.Kind));
        Assert.All(waves.Skip(2).Take(2).SelectMany(w => w.Chunks), c => Assert.Equal(GraphLoadKinds.Edge, c.Kind));
        Assert.True(waves.Take(4).All(w => w.HasMore));
        Assert.False(waves[4].HasMore);
        Assert.Empty(waves[4].Chunks);
    }

    [Fact]
    public void WaveAt_ItemsCarryTheRunsIdentity()
    {
        var item = Assert.Single(GraphLoadWavePlanner.WaveAt(Request, Plan(vertexChunks: 1, edgeChunks: 0), 0, 8).Chunks);

        Assert.Equal((Request.TenantId, Request.ScanId), (item.TenantId, item.ScanId));
    }

    [Fact]
    public void WaveAt_NoVertices_StartsWithEdges()
    {
        var wave = GraphLoadWavePlanner.WaveAt(Request, Plan(vertexChunks: 0, edgeChunks: 1), 0, 8);

        Assert.Equal(GraphLoadKinds.Edge, Assert.Single(wave.Chunks).Kind);
    }

    [Fact]
    public void WaveAt_EmptyPlan_HasNothingToDo()
    {
        var wave = GraphLoadWavePlanner.WaveAt(Request, Plan(0, 0), 0, 8);

        Assert.False(wave.HasMore);
    }

    [Fact]
    public async Task NextWaveStep_ReadsThePlanAndHandsBackTheFollowingWave()
    {
        var s3 = new InMemoryS3ObjectStore();
        s3.Objects[GraphLoadKeys.Plan(Request.TenantId, Request.ScanId)] = JsonSerializer.SerializeToUtf8Bytes(Plan(vertexChunks: 1, edgeChunks: 1));

        var next = await new GraphLoadNextWaveStep(s3, "graph-load", chunksPerWave: 8).RunAsync(new GraphLoadNextWaveRequest(Request, WaveIndex: 0));

        Assert.Equal(1, next.WaveIndex);
        Assert.Equal("e0", Assert.Single(next.Chunks).Key);
    }
}
