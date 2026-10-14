using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Thor.Graph;
using Thor.Workflows.GraphLoad.Steps;
using Xunit;

namespace Thor.Workflows.GraphLoad.Tests.Steps;

/// <summary>Covers <see cref="GraphLoadWriteChunkStep"/>: reading a chunk's rows by id and handing them to the writer.</summary>
public sealed class GraphLoadWriteChunkStepTests : GraphLoadDatabase
{
    private const string Bucket = "graph-load";

    private readonly InMemoryS3ObjectStore _s3 = new();
    private readonly RecordingGraphBatchWriter _writer = new();

    private GraphLoadWriteChunkStep Step() =>
        new(NullLoggerFactory.Instance, new FakeTenantConnectionManager(_ => Context), _s3, _writer, Bucket);

    private GraphLoadChunkRequest Chunk(string kind, params Guid[] ids)
    {
        var key = GraphLoadKeys.Chunk(TenantId, ScanId, kind, 0);
        _s3.Objects[key] = JsonSerializer.SerializeToUtf8Bytes(ids);
        return new GraphLoadChunkRequest(TenantId, ScanId, kind, key);
    }

    [Fact]
    public async Task RunAsync_VertexChunk_WritesEachRowsProjectionUnderTheRequestsTenant()
    {
        var manifestId = await SeedManifestAsync();
        var alice = await InsertAccountAsync(manifestId, "alice");
        var bob = await InsertAccountAsync(manifestId, "bob");

        var result = await Step().RunAsync(Context, Chunk(GraphLoadKinds.Account, alice, bob));

        var (tenantId, written) = Assert.Single(_writer.VertexCalls);
        Assert.Equal(TenantId, tenantId);
        Assert.Equal(new[] { alice, bob }.Order(), written.Select(v => v.Id).Order());
        Assert.All(written, v => Assert.Equal(GraphLoadKinds.Account, v.EntityType));
        Assert.Equal("alice", written.Single(v => v.Id == alice).Properties["displayName"]);
        Assert.Equal(true, written[0].Properties["isHuman"]);
        Assert.Equal(2, result.Elements);
        Assert.Empty(_writer.EdgeCalls);
    }

    [Fact]
    public async Task RunAsync_EdgeChunk_WritesEdgesWithTheirEndpoints_InTargetOrder()
    {
        var manifestId = await SeedManifestAsync();
        var (g1, g2) = (await InsertGroupAsync(manifestId, "g1"), await InsertGroupAsync(manifestId, "g2"));
        var dave = await InsertAccountAsync(manifestId, "dave");
        var toG1 = await InsertEdgeAsync(manifestId, (GraphLoadKinds.Account, dave), (GraphLoadKinds.Group, g1));
        var toG2 = await InsertEdgeAsync(manifestId, (GraphLoadKinds.Account, dave), (GraphLoadKinds.Group, g2));

        await Step().RunAsync(Context, Chunk(GraphLoadKinds.Edge, toG2, toG1));

        var written = Assert.Single(_writer.EdgeCalls).Edges;
        Assert.Equal(new[] { (toG1, g1), (toG2, g2) }.OrderBy(e => e.Item2).Select(e => e.Item1), written.Select(e => e.EdgeId));
        var edge = written.Single(e => e.EdgeId == toG1);
        Assert.Equal("MEMBER_OF", edge.RelType);
        Assert.Equal(new GraphVertexRef(GraphLoadKinds.Account, dave), edge.From);
        Assert.Equal(new GraphVertexRef(GraphLoadKinds.Group, g1), edge.To);
    }

    /// <summary>Deleted between start-run listing it and this chunk reading it: the scan that deleted it removes it from the graph.</summary>
    [Fact]
    public async Task RunAsync_ARowSoftDeletedSinceItWasListed_IsSkipped()
    {
        var manifestId = await SeedManifestAsync();
        var alice = await InsertAccountAsync(manifestId, "alice");
        var bob = await InsertAccountAsync(manifestId, "bob");
        await Context.Accounts.Where(a => a.Id == bob).ExecuteUpdateAsync(s => s.SetProperty(a => a.IsDeleted, true));

        await Step().RunAsync(Context, Chunk(GraphLoadKinds.Account, alice, bob));

        Assert.Equal([alice], Assert.Single(_writer.VertexCalls).Vertices.Select(v => v.Id));
    }

    /// <summary>Ids are minted from the request's tenant, so a key from another tenant's prefix must never be read.</summary>
    [Fact]
    public async Task RunAsync_KeyOutsideTheTenantsScan_FailsClosed()
    {
        var otherTenantKey = GraphLoadKeys.Chunk(Guid.NewGuid(), ScanId, GraphLoadKinds.Account, 0);
        _s3.Objects[otherTenantKey] = JsonSerializer.SerializeToUtf8Bytes(Array.Empty<Guid>());

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Step().RunAsync(Context, new GraphLoadChunkRequest(TenantId, ScanId, GraphLoadKinds.Account, otherTenantKey)));
        Assert.Empty(_writer.VertexCalls);
    }

    [Fact]
    public async Task RunAsync_UnknownKind_Throws()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => Step().RunAsync(Context, Chunk("entitlement", Guid.NewGuid())));
    }
}
