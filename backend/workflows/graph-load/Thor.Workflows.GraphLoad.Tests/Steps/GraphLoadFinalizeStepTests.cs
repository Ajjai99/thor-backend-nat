using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Thor.Graph;
using Thor.Workflows.Abstractions;
using Thor.Workflows.GraphLoad.Steps;
using Xunit;

namespace Thor.Workflows.GraphLoad.Tests.Steps;

/// <summary>
/// Covers the end of a scan's graph load — <see cref="GraphLoadFinalizeStep"/> applying the deletes and
/// closing the row, and <see cref="GraphLoadRecordFailureStep"/> closing it as failed — over a plan the
/// real start-run step wrote.
/// </summary>
public sealed class GraphLoadFinalizeStepTests : GraphLoadDatabase
{
    private const string Bucket = "graph-load";

    private readonly InMemoryS3ObjectStore _s3 = new();
    private readonly RecordingGraphVertexStore _vertexStore = new();
    private readonly RecordingGraphEdgeStore _edgeStore = new();

    private GraphLoadRequest Request => new(TenantId, ScanId);

    private Task StartAsync() =>
        new GraphLoadStartRunStep(NullLoggerFactory.Instance, new FakeTenantConnectionManager(_ => Context), _s3, Bucket, 10_000, 8)
            .RunAsync(Context, Request);

    private GraphLoadFinalizeStep Finalize() =>
        new(NullLoggerFactory.Instance, new FakeTenantConnectionManager(_ => Context), _s3, _vertexStore, _edgeStore, Bucket);

    [Fact]
    public async Task RunAsync_DeletesWhatTheScanSoftDeleted_AndCompletesTheRow()
    {
        var first = await SeedManifestAsync();
        var second = await SeedManifestAsync();
        var deletedAccount = await InsertAccountAsync(first, "bob", isDeleted: true);
        var deletedGroup = await InsertGroupAsync(second, "old-team", isDeleted: true);
        var alice = await InsertAccountAsync(second, "alice");
        var eng = await InsertGroupAsync(second, "eng");
        await InsertEdgeAsync(second, (GraphLoadKinds.Account, alice), (GraphLoadKinds.Group, eng), isDeleted: true);
        await StartAsync();

        await Finalize().RunAsync(Context, Request);

        Assert.Equal(
            new[] { (TenantId, GraphLoadKinds.Account, deletedAccount), (TenantId, GraphLoadKinds.Group, deletedGroup) }.Order(),
            _vertexStore.Deleted.Order());
        Assert.Equal(
            (TenantId, "MEMBER_OF", new GraphVertexRef(GraphLoadKinds.Account, alice), new GraphVertexRef(GraphLoadKinds.Group, eng)),
            Assert.Single(_edgeStore.Deleted));
        var row = await Context.Workflows.AsNoTracking().SingleAsync(w => w.WorkflowType == WorkflowTypes.GraphLoad);
        Assert.Equal("completed", row.Status);
        Assert.NotNull(row.CompletedAt);
    }

    [Fact]
    public async Task RunAsync_ADeleteFromAManifestWhoseIngestionFailed_IsNotApplied()
    {
        var good = await SeedManifestAsync();
        var failed = await SeedManifestAsync(ingestionStatus: "failed");
        await InsertAccountAsync(good, "alice");
        await InsertAccountAsync(failed, "bob", isDeleted: true);
        await StartAsync();

        await Finalize().RunAsync(Context, Request);

        Assert.Empty(_vertexStore.Deleted);
    }

    /// <summary>
    /// The reconcile workflow's deletions carry no manifest. They count, assets included, and the edges
    /// it soft-deleted alongside go with their vertex's drop rather than one by one.
    /// </summary>
    [Fact]
    public async Task RunAsync_ReconcileDeletions_DropTheirVertices_AndNotTheirEdgesSeparately()
    {
        var earlier = await SeedEarlierScanManifestAsync();
        var bob = await InsertAccountAsync(earlier, "bob");
        var eng = await InsertGroupAsync(earlier, "eng");
        var safe = await InsertAssetAsync(earlier, "old-safe");
        var membership = await InsertEdgeAsync(earlier, (GraphLoadKinds.Account, bob), (GraphLoadKinds.Group, eng));
        await SeedManifestAsync();
        await ReconcileDeleteAsync(GraphLoadKinds.Account, bob);
        await ReconcileDeleteAsync(GraphLoadKinds.Asset, safe);
        await ReconcileDeleteAsync(GraphLoadKinds.Edge, membership);
        await StartAsync();

        await Finalize().RunAsync(Context, Request);

        Assert.Equal(
            new[] { (TenantId, GraphLoadKinds.Account, bob), (TenantId, GraphLoadKinds.Asset, safe) }.Order(),
            _vertexStore.Deleted.Order());
        Assert.Empty(_edgeStore.Deleted);
    }

    [Fact]
    public async Task RecordFailure_ClosesTheRowAsFailed_WithTheCause()
    {
        var manifestId = await SeedManifestAsync();
        await InsertAccountAsync(manifestId, "carol");
        await StartAsync();

        await new GraphLoadRecordFailureStep(NullLoggerFactory.Instance, new FakeTenantConnectionManager(_ => Context))
            .RunAsync(Context, new GraphLoadFailureRequest(Request, new GraphLoadFailureCause("States.Timeout", "chunk timed out")));

        var row = await Context.Workflows.AsNoTracking().SingleAsync(w => w.WorkflowType == WorkflowTypes.GraphLoad);
        Assert.Equal("failed", row.Status);
        Assert.Equal("States.Timeout: chunk timed out", row.Error);
        Assert.NotNull(row.CompletedAt);
        Assert.Empty(_vertexStore.Deleted);
    }

    /// <summary>Start-run failed before it opened the row: there is nothing to close, and that is not an error.</summary>
    [Fact]
    public async Task RecordFailure_NoRowYet_IsANoOp()
    {
        await new GraphLoadRecordFailureStep(NullLoggerFactory.Instance, new FakeTenantConnectionManager(_ => Context))
            .RunAsync(Context, new GraphLoadFailureRequest(Request, null));

        Assert.False(await Context.Workflows.AnyAsync(w => w.WorkflowType == WorkflowTypes.GraphLoad));
    }
}
