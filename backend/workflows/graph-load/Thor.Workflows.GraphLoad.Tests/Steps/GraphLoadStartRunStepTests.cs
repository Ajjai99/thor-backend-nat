using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Thor.Workflows.Abstractions;
using Thor.Workflows.GraphLoad.Steps;
using Xunit;

namespace Thor.Workflows.GraphLoad.Tests.Steps;

/// <summary>Covers <see cref="GraphLoadStartRunStep"/> and the write set it lists, against a real Postgres.</summary>
public sealed class GraphLoadStartRunStepTests : GraphLoadDatabase
{
    private const string Bucket = "graph-load";

    private readonly InMemoryS3ObjectStore _s3 = new();

    private Task<GraphLoadWave> StartAsync(int idsPerChunk = 10_000, int chunksPerWave = 8) =>
        new GraphLoadStartRunStep(NullLoggerFactory.Instance, new FakeTenantConnectionManager(_ => Context), _s3, Bucket, idsPerChunk, chunksPerWave)
            .RunAsync(Context, new GraphLoadRequest(TenantId, ScanId));

    private GraphLoadPlan ReadPlan() =>
        JsonSerializer.Deserialize<GraphLoadPlan>(_s3.Objects[GraphLoadKeys.Plan(TenantId, ScanId)])!;

    /// <summary>Every id the plan lists for <paramref name="kind"/>, across its chunks, in listed order.</summary>
    private List<Guid> Listed(string kind) =>
        [.. ReadPlan().VertexChunks.Concat(ReadPlan().EdgeChunks)
            .Where(c => c.Kind == kind)
            .SelectMany(c => JsonSerializer.Deserialize<List<Guid>>(_s3.Objects[c.Key])!)];

    [Fact]
    public async Task RunAsync_ListsTheScansChangedLiveVerticesByKind_AndNotTheDeletedOnes()
    {
        var manifestId = await SeedManifestAsync();
        var alice = await InsertAccountAsync(manifestId, "alice");
        await InsertAccountAsync(manifestId, "bob", isDeleted: true);
        var eng = await InsertGroupAsync(manifestId, "eng");
        var safe = await InsertAssetAsync(manifestId, "safe");

        await StartAsync();

        Assert.Equal([alice], Listed(GraphLoadKinds.Account));
        Assert.Equal([eng], Listed(GraphLoadKinds.Group));
        Assert.Equal([safe], Listed(GraphLoadKinds.Asset));
        Assert.Equal([manifestId], ReadPlan().ScanManifestIds);
    }

    /// <summary>mergeE fails a whole request over one missing endpoint, so an endpoint from an earlier scan is written again.</summary>
    [Fact]
    public async Task RunAsync_AnEdgeIntoAVertexFromAnEarlierScan_WritesThatVertexToo()
    {
        var earlier = await SeedEarlierScanManifestAsync();
        var manifestId = await SeedManifestAsync();
        var ops = await InsertGroupAsync(earlier, "ops");
        var erin = await InsertAccountAsync(manifestId, "erin");
        var edge = await InsertEdgeAsync(manifestId, (GraphLoadKinds.Account, erin), (GraphLoadKinds.Group, ops));

        await StartAsync();

        Assert.Equal([ops], Listed(GraphLoadKinds.Group));
        Assert.Equal([edge], Listed(GraphLoadKinds.Edge));
    }

    [Fact]
    public async Task RunAsync_AnEdgeWithADeletedOrUnheldEndpoint_IsNotWritten_ButItsLiveEndIs()
    {
        var earlier = await SeedEarlierScanManifestAsync();
        var manifestId = await SeedManifestAsync();
        var gone = await InsertGroupAsync(earlier, "gone", isDeleted: true);
        var oldTeam = await InsertGroupAsync(earlier, "old-team");
        var erin = await InsertAccountAsync(manifestId, "erin");
        await InsertEdgeAsync(manifestId, (GraphLoadKinds.Account, erin), (GraphLoadKinds.Group, gone));
        await InsertEdgeAsync(manifestId, (GraphLoadKinds.Group, oldTeam), ("entitlement", Guid.NewGuid()), relType: "HAS_ENTITLEMENT");

        await StartAsync();

        Assert.Empty(Listed(GraphLoadKinds.Edge));
        Assert.Equal([oldTeam], Listed(GraphLoadKinds.Group)); // live, so harmless to write again
        Assert.Equal([erin], Listed(GraphLoadKinds.Account));
    }

    [Fact]
    public async Task RunAsync_ListsEdgesByTarget_SoOneGroupsMembersStayTogether()
    {
        var manifestId = await SeedManifestAsync();
        var groups = new[] { await InsertGroupAsync(manifestId, "g1"), await InsertGroupAsync(manifestId, "g2") };
        var accounts = new[] { await InsertAccountAsync(manifestId, "a1"), await InsertAccountAsync(manifestId, "a2") };
        foreach (var account in accounts)
        {
            foreach (var group in groups)
            {
                await InsertEdgeAsync(manifestId, (GraphLoadKinds.Account, account), (GraphLoadKinds.Group, group));
            }
        }

        await StartAsync();

        var targets = await Context.Edges.AsNoTracking().ToDictionaryAsync(e => e.Id, e => e.ToId);
        var listedTargets = Listed(GraphLoadKinds.Edge).Select(id => targets[id]).ToList();
        Assert.Equal(4, listedTargets.Count);
        Assert.Equal(listedTargets[0], listedTargets[1]);
        Assert.Equal(listedTargets[2], listedTargets[3]);
        Assert.NotEqual(listedTargets[1], listedTargets[2]);
    }

    [Fact]
    public async Task RunAsync_AManifestWhoseIngestionFailed_ContributesNothing()
    {
        var good = await SeedManifestAsync();
        var failed = await SeedManifestAsync(ingestionStatus: "failed");
        var alice = await InsertAccountAsync(good, "alice");
        await InsertAccountAsync(failed, "bob");

        await StartAsync();

        Assert.Equal([alice], Listed(GraphLoadKinds.Account));
        Assert.Equal([good], ReadPlan().ScanManifestIds);
    }

    [Fact]
    public async Task RunAsync_SpillsIdsAcrossChunks_AndHandsBackTheFirstVertexWave()
    {
        var manifestId = await SeedManifestAsync();
        for (var i = 0; i < 3; i++)
        {
            await InsertAccountAsync(manifestId, $"user{i}");
        }
        await InsertGroupAsync(manifestId, "eng");

        var wave = await StartAsync(idsPerChunk: 2, chunksPerWave: 2);

        var plan = ReadPlan();
        Assert.Equal([GraphLoadKinds.Account, GraphLoadKinds.Account, GraphLoadKinds.Group], plan.VertexChunks.Select(c => c.Kind));
        Assert.All(plan.VertexChunks, c => Assert.StartsWith(GraphLoadKeys.ScanPrefix(TenantId, ScanId), c.Key));
        Assert.Equal(0, wave.WaveIndex);
        Assert.True(wave.HasMore);
        Assert.Equal(plan.VertexChunks.Take(2).Select(c => c.Key), wave.Chunks.Select(c => c.Key));
    }

    [Fact]
    public async Task RunAsync_OpensTheScansGraphLoadRow_AndReopensItOnRetry()
    {
        var manifestId = await SeedManifestAsync();
        await InsertAccountAsync(manifestId, "carol");

        await StartAsync();
        var row = await Context.Workflows.AsNoTracking().SingleAsync(w => w.WorkflowType == WorkflowTypes.GraphLoad);
        Assert.Equal(ScanId, row.ScanId);
        Assert.Null(row.ScanManifestId);
        await Context.Workflows.Where(w => w.Id == row.Id).ExecuteUpdateAsync(s => s
            .SetProperty(w => w.Status, "failed").SetProperty(w => w.Error, "earlier attempt").SetProperty(w => w.CompletedAt, DateTimeOffset.UtcNow));

        await StartAsync();

        var reopened = await Context.Workflows.AsNoTracking().SingleAsync(w => w.WorkflowType == WorkflowTypes.GraphLoad);
        Assert.Equal(row.Id, reopened.Id);
        Assert.Equal("started", reopened.Status);
        Assert.Null(reopened.Error);
        Assert.Null(reopened.CompletedAt);
    }

    [Fact]
    public async Task RunAsync_NothingChanged_HandsBackAnEmptyWave()
    {
        var wave = await StartAsync();

        Assert.False(wave.HasMore);
        Assert.Empty(wave.Chunks);
    }
}
