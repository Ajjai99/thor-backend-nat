using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Thor.DataLayer.Models.Tenants;
using Thor.Workflows.Abstractions;
using Thor.Workflows.Reconcile.Steps;
using Xunit;

namespace Thor.Workflows.Reconcile.Tests;

/// <summary>A whole reconcile run, driven step by step as the state machine drives it, over a real tenant database.</summary>
public sealed class ReconcileRunTests : ReconcileDatabase
{
    [Fact]
    public async Task AnAbsentAccountWithinTheThreshold_IsDeletedWithEverythingThatKeepsItDeleted()
    {
        await SeedSeenAccountsAsync("present", 19);
        var leaver = await AccountAsync("leaver", seen: false);
        var group = await GroupAsync("eng");
        var membership = await EdgeAsync((EntityTypes.Account, leaver), (EntityTypes.Group, group));
        await AliasAndParkedRefAsync(EntityTypes.Account, leaver);

        await RunAsync();

        var row = await AccountRowAsync(leaver);
        Assert.True(row.IsDeleted);
        Assert.Equal("", row.ContentHash);
        Assert.Contains("\"EdgeRefs\": []", row.RawAttributes);
        Assert.True((await Context.Edges.AsNoTracking().SingleAsync(e => e.Id == membership)).IsDeleted);
        Assert.False(await Context.EntityAliases.AnyAsync(a => a.EntityId == leaver));
        Assert.False(await Context.OneSidedRefs.AnyAsync(r => r.OwnerId == leaver));

        // Scan-wide events, the ones the graph load and PAI read: one for the account, one for its edge.
        var events = await Context.IngestChangeEvents.AsNoTracking().Where(e => e.ScanId == ScanId).ToListAsync();
        Assert.All(events, e => Assert.Equal((null, "deleted"), (e.ScanManifestId, e.ChangeType)));
        Assert.Equal(new[] { ("account", leaver), ("edge", membership) }.Order(), events.Select(e => (e.EntityType, e.EntityId)).Order());

        Assert.False((await AccountRowAsync(await Context.Accounts.Where(a => a.NativeId == "present-0").Select(a => a.Id).SingleAsync())).IsDeleted);
        Assert.False((await Context.Grps.AsNoTracking().SingleAsync(g => g.Id == group)).IsDeleted);

        var decision = (await DecisionsAsync()).Single(d => d.EntityType == EntityTypes.Account);
        Assert.Equal((ReconcileOutcome.Deleted, 20L, 1L, (short)5), (decision.Outcome, decision.LiveCount, decision.AbsentCount, decision.ThresholdPercent));

        Assert.False(await Context.ScanSeenEntities.AnyAsync(s => s.ScanId == ScanId));
        Assert.False(await Context.ReconcilePendings.AnyAsync());
        var workflow = await WorkflowRowAsync();
        Assert.Equal("completed", workflow.Status);
        Assert.NotNull(workflow.CompletedAt);
    }

    /// <summary>Ownership never revisits an entity no manifest carries, so the deletion retires its owner.</summary>
    [Fact]
    public async Task ADeletedEntitysActiveOwner_IsRetiredWithAnEvent_AndNoOtherOwnerIsTouched()
    {
        await SeedSeenAccountsAsync("present", 19);
        var leaver = await AccountAsync("leaver", seen: false);
        var stayer = await Context.Accounts.Where(a => a.NativeId == "present-0").Select(a => a.Id).SingleAsync();
        var leaversOwner = await OwnerAsync(EntityTypes.Account, leaver);
        var leaversOldOwner = await OwnerAsync(EntityTypes.Account, leaver, isActive: false);
        var staysOwner = await OwnerAsync(EntityTypes.Account, stayer);

        await RunAsync();

        var assignments = await Context.PartyAssignments.AsNoTracking().ToDictionaryAsync(a => a.Id, a => a.IsActive);
        Assert.False(assignments[leaversOwner]);
        Assert.False(assignments[leaversOldOwner]);
        Assert.True(assignments[staysOwner]);

        // One event, for the owner that was active; the one already retired is left alone.
        var retired = Assert.Single(await Context.PartyAssignmentEvents.AsNoTracking().ToListAsync());
        Assert.Equal(
            (leaversOwner, "entity_deleted", "reconcile_workflow"),
            (retired.AssignmentId, retired.EventType, retired.Actor));
        Assert.NotNull(retired.PreviousIdentityId);
        Assert.Null(retired.NewIdentityId);
    }

    [Fact]
    public async Task OverTheThreshold_RowsAreProposedNotDeleted_AndTheRunSaysSo()
    {
        await SeedSeenAccountsAsync("present", 18);
        var gone1 = await AccountAsync("gone-1", seen: false);
        var gone2 = await AccountAsync("gone-2", seen: false);
        var gone1Owner = await OwnerAsync(EntityTypes.Account, gone1);

        await RunAsync();

        foreach (var id in new[] { gone1, gone2 })
        {
            var row = await AccountRowAsync(id);
            Assert.False(row.IsDeleted);
            Assert.NotNull(row.DeletionProposedAt);
            Assert.Equal(ScanId, row.DeletionProposedScanId);
        }

        // A proposal changes nothing else: no events, and its owner stays.
        Assert.Empty(await Context.IngestChangeEvents.ToListAsync());
        Assert.True((await Context.PartyAssignments.AsNoTracking().SingleAsync(a => a.Id == gone1Owner)).IsActive);
        Assert.Empty(await Context.PartyAssignmentEvents.ToListAsync());
        var decision = (await DecisionsAsync()).Single(d => d.EntityType == EntityTypes.Account);
        Assert.Equal((ReconcileOutcome.Proposed, 20L, 2L), (decision.Outcome, decision.LiveCount, decision.AbsentCount));
        Assert.Equal(ReconcileStatuses.CompletedWithProposals, (await WorkflowRowAsync()).Status);
    }

    [Fact]
    public async Task AProposedRowSeenAgainByTheNextScan_IsWithdrawn()
    {
        await SeedSeenAccountsAsync("present", 8);
        var back = await AccountAsync("back", seen: false);
        await RunAsync();
        Assert.NotNull((await AccountRowAsync(back)).DeletionProposedAt);

        // The next scan sees everything again.
        var nextScan = await AddScanAsync(ingestionCompleted: true);
        await AddTaskAsync(nextScan, SourceId, ScanTaskStatus.Completed);
        foreach (var nativeId in await Context.Accounts.Select(a => a.NativeId).ToListAsync())
        {
            await SeenAsync(EntityTypes.Account, nativeId, scanId: nextScan);
        }

        await new ReconcileStartRunStep(NullLoggerFactory.Instance, null!, 2, 2).RunAsync(Context, new ReconcileRequest(TenantId, nextScan));

        Assert.Null((await AccountRowAsync(back)).DeletionProposedAt);
        var decision = await Context.ReconcileDecisions.AsNoTracking().SingleAsync(d => d.ScanId == nextScan && d.EntityType == EntityTypes.Account);
        Assert.Equal((ReconcileOutcome.NothingAbsent, 1), (decision.Outcome, decision.ProposalsWithdrawn));
    }

    [Fact]
    public async Task AThresholdOfZero_ProposesEvenASingleAbsence()
    {
        await SetThresholdAsync(SourceId, 0);
        await SeedSeenAccountsAsync("present", 99);
        var gone = await AccountAsync("gone", seen: false);

        await RunAsync();

        Assert.False((await AccountRowAsync(gone)).IsDeleted);
        Assert.NotNull((await AccountRowAsync(gone)).DeletionProposedAt);
    }

    [Theory]
    [InlineData(1, 20, 5, false)] // exactly 5% is within a 5% threshold
    [InlineData(2, 20, 5, true)]
    [InlineData(1, 1000, 0, true)]
    [InlineData(0, 10, 0, false)]
    [InlineData(25, 100, 25, false)]
    public void ExceedsThreshold_IsExactIntegerArithmetic(long absent, long live, int threshold, bool exceeds) =>
        Assert.Equal(exceeds, Reconciliation.AbsencePlanner.ExceedsThreshold(absent, live, (short)threshold));

    [Fact]
    public async Task ASourceWhoseTaskFailed_IsSkippedAndUntouched()
    {
        var failing = await AddSourceAsync("cyberark");
        await AddTaskAsync(ScanId, failing, ScanTaskStatus.Failed);
        var untouched = await AccountAsync("vault-user", seen: false, sourceId: failing);
        await SeedSeenAccountsAsync("present", 3);

        await RunAsync();

        Assert.False((await AccountRowAsync(untouched)).IsDeleted);
        Assert.Null((await AccountRowAsync(untouched)).DeletionProposedAt);
        var decisions = await DecisionsAsync(failing);
        Assert.Equal(EntityTypes.All.Count, decisions.Count);
        Assert.All(decisions, d => Assert.Equal((ReconcileOutcome.Skipped, "task_not_successful"), (d.Outcome, d.SkipReason)));
    }

    [Fact]
    public async Task ASourceAlreadyCoveredByANewerScan_IsSkipped()
    {
        await SeedSeenAccountsAsync("present", 19);
        var gone = await AccountAsync("gone", seen: false);
        var newer = await AddScanAsync(ingestionCompleted: false);
        await AddTaskAsync(newer, SourceId, ScanTaskStatus.InProgress, DateTimeOffset.UtcNow.AddMinutes(5));

        await RunAsync();

        Assert.False((await AccountRowAsync(gone)).IsDeleted);
        Assert.All(await DecisionsAsync(), d => Assert.Equal("superseded_by_newer_scan", d.SkipReason));
    }

    /// <summary>
    /// The race the superseded guard cannot see: a newer scan starts after this run decided, and promotes
    /// the row before this run's wave reaches it. The newer scan's view wins.
    /// </summary>
    [Fact]
    public async Task AnEntityAScanStillIngestingHasSeen_IsNotDeleted()
    {
        await SeedSeenAccountsAsync("present", 19);
        var recreated = await AccountAsync("recreated", seen: false);
        var start = await new ReconcileStartRunStep(NullLoggerFactory.Instance, null!, 10, 2).RunAsync(Context, Request);

        var newer = await AddScanAsync(ingestionCompleted: false);
        await SeenAsync(EntityTypes.Account, "recreated", scanId: newer);
        foreach (var chunk in start.Chunks)
        {
            await new ReconcileDeleteChunkStep(NullLoggerFactory.Instance, null!).RunAsync(Context, chunk);
        }

        Assert.False((await AccountRowAsync(recreated)).IsDeleted);
    }

    [Fact]
    public async Task ARerunChunk_ChangesNothingTwice()
    {
        await SeedSeenAccountsAsync("present", 19);
        var leaver = await AccountAsync("leaver", seen: false);
        await EdgeAsync((EntityTypes.Account, leaver), (EntityTypes.Group, await GroupAsync("eng")));
        await OwnerAsync(EntityTypes.Account, leaver);
        var start = await new ReconcileStartRunStep(NullLoggerFactory.Instance, null!, 10, 2).RunAsync(Context, Request);
        var chunk = Assert.Single(start.Chunks);

        var first = await new ReconcileDeleteChunkStep(NullLoggerFactory.Instance, null!).RunAsync(Context, chunk);
        var again = await new ReconcileDeleteChunkStep(NullLoggerFactory.Instance, null!).RunAsync(Context, chunk);

        Assert.Equal((1, 1), (first.EntitiesDeleted, first.EdgesDeleted));
        Assert.Equal((0, 0), (again.EntitiesDeleted, again.EdgesDeleted));
        Assert.Equal(2, await Context.IngestChangeEvents.CountAsync());
        Assert.Equal(1, await Context.PartyAssignmentEvents.CountAsync());
    }

    [Fact]
    public async Task AnEdgeBetweenTwoDeletedEntities_IsDeletedOnce()
    {
        await SeedSeenAccountsAsync("present", 40);
        var leaver = await AccountAsync("leaver", seen: false);
        var oldGroup = await GroupAsync("old-team", seen: false);
        await SeedSeenGroupsAsync(19);
        await EdgeAsync((EntityTypes.Account, leaver), (EntityTypes.Group, oldGroup));

        await RunAsync(rowsPerChunk: 1, chunksPerWave: 2);

        Assert.Equal(1, await Context.IngestChangeEvents.CountAsync(e => e.EntityType == "edge"));
        Assert.True((await Context.Grps.AsNoTracking().SingleAsync(g => g.Id == oldGroup)).IsDeleted);
    }

    /// <summary>
    /// A wave's chunks run at once, and every account chunk here shares edges with every group chunk.
    /// Taking the edge locks in one global order is what keeps them from deadlocking; each edge is still
    /// deleted exactly once.
    /// </summary>
    [Fact]
    public async Task ConcurrentChunksSharingEdges_DoNotDeadlock_AndDeleteEachEdgeOnce()
    {
        await SetThresholdAsync(SourceId, 100);
        var accounts = new List<Guid>();
        var groups = new List<Guid>();
        for (var i = 0; i < 20; i++)
        {
            accounts.Add(await AccountAsync($"leaver-{i}", seen: false));
            groups.Add(await GroupAsync($"old-team-{i}", seen: false));
        }

        await AccountAsync("present"); // a ledger, so the scan is swept
        foreach (var account in accounts)
        {
            foreach (var group in groups)
            {
                Context.Edges.Add(new Edge
                {
                    Id = Guid.NewGuid(), FromType = EntityTypes.Account, FromId = account, ToType = EntityTypes.Group, ToId = group,
                    RelType = "MEMBER_OF", ContentHash = "hash", CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
                });
            }
        }

        await Context.SaveChangesAsync();
        var start = await new ReconcileStartRunStep(NullLoggerFactory.Instance, null!, 5, 8).RunAsync(Context, Request);
        Assert.Equal(8, start.Chunks.Count);

        await Task.WhenAll(start.Chunks.Select(async chunk =>
        {
            await using var db = NewContext();
            await new ReconcileDeleteChunkStep(NullLoggerFactory.Instance, null!).RunAsync(db, chunk);
        }));

        Assert.Equal(400, await Context.IngestChangeEvents.CountAsync(e => e.EntityType == "edge"));
        Assert.Equal(0, await Context.Edges.CountAsync(e => !e.IsDeleted));
    }

    [Fact]
    public async Task AnAbsentAsset_IsSoftDeleted()
    {
        for (var i = 0; i < 19; i++)
        {
            await AssetAsync($"safe-{i}");
        }

        var decommissioned = await AssetAsync("decommissioned", seen: false);

        await RunAsync();

        Assert.True((await Context.Assets.AsNoTracking().SingleAsync(a => a.Id == decommissioned)).IsDeleted);
    }

    [Fact]
    public async Task AScanWithNoLedger_SweepsNothing_AndStillCompletes()
    {
        await AccountAsync("never-recorded", seen: false);

        var wave = await RunAsync();

        Assert.False(wave.HasMore);
        Assert.False((await Context.Accounts.AsNoTracking().SingleAsync()).IsDeleted);
        Assert.Empty(await Context.ReconcileDecisions.ToListAsync());
        Assert.Equal("completed", (await WorkflowRowAsync()).Status);
    }

    [Fact]
    public async Task AScanStillIngesting_IsRefused()
    {
        var ingesting = await AddScanAsync(ingestionCompleted: false);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new ReconcileStartRunStep(NullLoggerFactory.Instance, null!, 2, 2).RunAsync(Context, new ReconcileRequest(TenantId, ingesting)));
    }

    [Fact]
    public async Task NoScanId_IsRefusedBeforeAnythingIsWritten()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new ReconcileStartRunStep(NullLoggerFactory.Instance, null!, 2, 2).RunAsync(Context, new ReconcileRequest(TenantId, Guid.Empty)));

        Assert.False(await Context.Workflows.AnyAsync());
    }

    /// <summary>A redrive after the scan's graph load started would delete rows whose vertices nothing will drop.</summary>
    [Fact]
    public async Task OnceTheGraphLoadHasStarted_AChunkRefusesToDelete()
    {
        await SeedSeenAccountsAsync("present", 19);
        var leaver = await AccountAsync("leaver", seen: false);
        var start = await new ReconcileStartRunStep(NullLoggerFactory.Instance, null!, 10, 2).RunAsync(Context, Request);
        Context.Workflows.Add(new WorkflowEntity
        {
            Id = Guid.NewGuid(), ScanId = ScanId, WorkflowType = WorkflowTypes.GraphLoad, Trigger = WorkflowTriggers.StepFunctions,
            Status = "started", StartedAt = DateTimeOffset.UtcNow,
        });
        await Context.SaveChangesAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new ReconcileDeleteChunkStep(NullLoggerFactory.Instance, null!).RunAsync(Context, Assert.Single(start.Chunks)));
        Assert.False((await AccountRowAsync(leaver)).IsDeleted);
    }

    [Fact]
    public async Task RecordFailure_OpensTheRowIfStartRunNeverDid_AndClosesItFailed()
    {
        await new ReconcileRecordFailureStep(NullLoggerFactory.Instance, null!)
            .RunAsync(Context, new ReconcileFailureRequest(Request, new ReconcileFailureCause("States.Timeout", "chunk timed out")));

        var workflow = await WorkflowRowAsync();
        Assert.Equal(("failed", "States.Timeout: chunk timed out"), (workflow.Status, workflow.Error));
        Assert.NotNull(workflow.CompletedAt);
    }

    private async Task SeedSeenGroupsAsync(int count)
    {
        for (var i = 0; i < count; i++)
        {
            await GroupAsync($"grp-{i}");
        }
    }
}
