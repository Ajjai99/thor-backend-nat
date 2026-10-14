using Microsoft.EntityFrameworkCore;
using Thor.Workflows.Pai.Tests.Fixtures;
using Xunit;

namespace Thor.Workflows.Pai.Tests;

/// <summary>
/// PAI end to end over Postgres and an in-memory graph, run the way the state machine runs it. The
/// oracle is <see cref="PaiWorld.ExpectedRoutesAsync"/> — every simple path, by brute force — so each
/// test asserts both what PAI wrote and that it is exactly what a fresh full recompute would find.
/// </summary>
public sealed class PaiPipelineTests(TenantDatabaseFixture db) : IClassFixture<TenantDatabaseFixture>, IAsyncLifetime
{
    public Task InitializeAsync() => db.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task FirstRun_MaterialisesEveryRoute_ThroughNestingDiamondsAndCycles()
    {
        var world = new PaiWorld(db);
        var domainAdmins = world.AddGroup("Domain Admins");
        var g1 = world.AddGroup("Tier1");
        var g2 = world.AddGroup("Tier2");
        var g3 = world.AddGroup("Tier3");
        await world.ConnectAsync(g1, domainAdmins);
        await world.ConnectAsync(g2, g1);
        await world.ConnectAsync(g2, domainAdmins); // diamond: g2 reaches Domain Admins two ways
        await world.ConnectAsync(g3, g2);
        await world.ConnectAsync(g2, g3);           // cycle
        var nested = await world.AddAccountAsync("nested");
        var direct = await world.AddAccountAsync("direct");
        var outsider = await world.AddAccountAsync("outsider");
        await world.ConnectAsync(nested, g2);
        await world.ConnectAsync(direct, domainAdmins);
        await world.ConnectAsync(outsider, world.AddGroup("Everyone"));

        await world.RunPaiAsync(await world.CompleteScanAsync());

        var actual = await world.ActualRoutesAsync();
        Assert.Equal(await world.ExpectedRoutesAsync(), actual);
        Assert.Equal(2, actual.Count(route => route.Contains($"|{nested}>")));
        Assert.DoesNotContain(actual, route => route.Contains(outsider.ToString()));

        // The first run is full for each of the three shipped definitions.
        Assert.Equal(3, world.Graph.LabelScans);
    }

    [Fact]
    public async Task AGroupNamedAdministrators_IsMatchedOnlyByItsOwnConnectorsDefinition()
    {
        var world = new PaiWorld(db);
        var localAdmins = world.AddGroup("Administrators", connectorType: 402);
        var account = await world.AddAccountAsync("local");
        await world.ConnectAsync(account, localAdmins);

        await world.RunPaiAsync(await world.CompleteScanAsync());

        await using var context = db.NewContext();
        var definitions = await context.PaiResults.Select(r => r.PrivilegeDefinition.ConnectorType).Distinct().ToListAsync();
        Assert.Equal([(short)402], definitions);
        Assert.Equal(await world.ExpectedRoutesAsync(), await world.ActualRoutesAsync());
    }

    [Fact]
    public async Task ASafeGrant_IsASingleHop()
    {
        var world = new PaiWorld(db);
        var safe = world.AddAsset("cyberark_safe", connectorType: 401);
        var holder = await world.AddAccountAsync("holder");
        var viaGroup = await world.AddAccountAsync("via-group");
        var group = world.AddGroup("SafeUsers", connectorType: 401);
        await world.ConnectAsync(holder, safe, PaiWorld.HasAccess);
        await world.ConnectAsync(group, safe, PaiWorld.HasAccess);
        await world.ConnectAsync(viaGroup, group);

        await world.RunPaiAsync(await world.CompleteScanAsync());

        var actual = await world.ActualRoutesAsync();
        Assert.Single(actual);
        Assert.Contains(holder.ToString(), actual.Single());
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task IncrementalRuns_MatchAFullRecompute_AcrossRandomChanges(int seed)
    {
        var random = new Random(seed);
        var world = new PaiWorld(db);
        string[] names = ["Domain Admins", "Enterprise Admins", "Backup Operators", "Staff", "Helpdesk", "Contractors"];
        var groups = Enumerable.Range(0, 14).Select(i => world.AddGroup(names[i % names.Length])).ToList();
        var accounts = new List<Guid>();
        for (var i = 0; i < 20; i++)
        {
            accounts.Add(await world.AddAccountAsync($"user{i}"));
        }

        for (var i = 0; i < 25; i++)
        {
            await world.ConnectAsync(Pick(groups), Pick(groups));
        }

        for (var i = 0; i < 35; i++)
        {
            await world.ConnectAsync(Pick(accounts), Pick(groups));
        }

        await world.RunPaiAsync(await world.CompleteScanAsync());
        Assert.Equal(await world.ExpectedRoutesAsync(), await world.ActualRoutesAsync());
        var labelScansAfterFullRun = world.Graph.LabelScans;

        for (var step = 0; step < 12; step++)
        {
            for (var change = 0; change < 3; change++)
            {
                await ApplyRandomChangeAsync();
            }

            await world.RunPaiAsync(await world.CompleteScanAsync());
            Assert.Equal(await world.ExpectedRoutesAsync(), await world.ActualRoutesAsync());
        }

        // Every later run was incremental: none of them matched a pattern across the whole graph.
        Assert.Equal(labelScansAfterFullRun, world.Graph.LabelScans);

        async Task ApplyRandomChangeAsync()
        {
            var liveGroups = groups.Where(g => world.Graph.Vertices.ContainsKey(g)).ToList();
            var liveAccounts = accounts.Where(a => world.Graph.Vertices.ContainsKey(a)).ToList();
            var edges = world.Graph.Edges.ToList();
            switch (random.Next(7))
            {
                case 0: await world.ConnectAsync(Pick(liveGroups), Pick(liveGroups)); break;
                case 1: await world.ConnectAsync(Pick(liveAccounts), Pick(liveGroups)); break;
                case 2 when edges.Count > 0:
                    var edge = edges[random.Next(edges.Count)];
                    await world.DisconnectAsync(edge.From, edge.To, edge.Rel);
                    break;
                case 3: world.Rename(Pick(liveGroups), names[random.Next(names.Length)]); break;
                case 4 when liveGroups.Count > 4: world.DeleteGroup(Pick(liveGroups)); break;
                case 5 when liveAccounts.Count > 4: await world.DeleteAccountAsync(Pick(liveAccounts)); break;
                default:
                    var account = await world.AddAccountAsync($"late{random.Next()}");
                    accounts.Add(account);
                    await world.ConnectAsync(account, Pick(liveGroups));
                    break;
            }
        }

        Guid Pick(List<Guid> from) => from[random.Next(from.Count)];
    }

    [Fact]
    public async Task ARunWithNoChanges_RewritesNothing()
    {
        var world = new PaiWorld(db);
        var domainAdmins = world.AddGroup("Domain Admins");
        await world.ConnectAsync(await world.AddAccountAsync("admin"), domainAdmins);
        await world.RunPaiAsync(await world.CompleteScanAsync());
        var before = await ComputedAtAsync();

        await world.RunPaiAsync(await world.CompleteScanAsync());

        Assert.Equal(before, await ComputedAtAsync());
        Assert.Equal(await world.ExpectedRoutesAsync(), await world.ActualRoutesAsync());
    }

    [Fact]
    public async Task AFailedChunk_KeepsItsTargetsRoutes_AndTheNextRunRedoesItsScan()
    {
        var world = new PaiWorld(db);
        var domainAdmins = world.AddGroup("Domain Admins");
        var admin = await world.AddAccountAsync("admin");
        await world.ConnectAsync(admin, domainAdmins);
        await world.RunPaiAsync(await world.CompleteScanAsync());

        await world.DisconnectAsync(admin, domainAdmins);
        var failedScan = await world.CompleteScanAsync();
        await world.RunPaiAsync(failedScan, failChunk: _ => true);

        // The revoked route survives: its target's chunk failed, so it was never re-proven or pruned.
        Assert.Single(await world.ActualRoutesAsync());
        Assert.Equal("completed_with_errors", await world.RunStatusAsync(failedScan));

        // The failed run covered nothing, so a run with no new changes re-reads that scan and prunes it.
        await world.RunPaiAsync(await world.CompleteScanAsync());
        Assert.Empty(await world.ActualRoutesAsync());
    }

    [Fact]
    public async Task AnEditedDefinition_IsRecomputedInFull_AndLosesRoutesItNoLongerProves()
    {
        var world = new PaiWorld(db);
        var domainAdmins = world.AddGroup("Domain Admins");
        await world.ConnectAsync(await world.AddAccountAsync("admin"), domainAdmins);
        await world.RunPaiAsync(await world.CompleteScanAsync());

        // A route no change event points at, as an old definition might have left behind.
        await using (var context = db.NewContext())
        {
            var definition = await context.PrivilegeDefinitions.SingleAsync(d => d.ConnectorType == 308);
            var existing = await context.PaiResults.AsNoTracking().FirstAsync();
            context.PaiResults.Add(new()
            {
                Id = Guid.NewGuid(), AccountId = existing.AccountId, TargetId = Guid.NewGuid(), TargetType = "grp", AccessPath = "[]",
                AccessPathHash = "stale", PrivilegeDefinitionId = definition.Id, IsPrivileged = true, IsActive = true,
                RunId = Guid.NewGuid(), ComputedAt = DateTimeOffset.UtcNow.AddDays(-1),
            });
            definition.ComputedHash = "an older edit";
            await context.SaveChangesAsync();
        }

        var labelScans = world.Graph.LabelScans;
        await world.RunPaiAsync(await world.CompleteScanAsync());

        Assert.Equal(labelScans + 1, world.Graph.LabelScans);
        Assert.Equal(await world.ExpectedRoutesAsync(), await world.ActualRoutesAsync());
    }

    [Fact]
    public async Task OverlappingRuns_DoNotPruneEachOthersRoutes()
    {
        var world = new PaiWorld(db);
        var domainAdmins = world.AddGroup("Domain Admins");
        await world.ConnectAsync(await world.AddAccountAsync("first"), domainAdmins);
        await world.RunPaiAsync(await world.CompleteScanAsync());

        await world.ConnectAsync(await world.AddAccountAsync("second"), domainAdmins);
        var (earlier, earlierWave) = await world.StartPaiAsync(await world.CompleteScanAsync());

        // The earlier run writes its routes, then a later run rewrites every one of them and finishes
        // before the earlier run prunes. Pruning by run id would now delete all of them.
        await world.FinishPaiAsync(earlier, earlierWave, beforeFinalize: async () =>
        {
            await world.ConnectAsync(await world.AddAccountAsync("third"), domainAdmins);
            await world.RunPaiAsync(await world.CompleteScanAsync());
        });

        Assert.Equal(3, (await world.ActualRoutesAsync()).Count);
        Assert.Equal(await world.ExpectedRoutesAsync(), await world.ActualRoutesAsync());
    }

    private async Task<List<DateTimeOffset>> ComputedAtAsync()
    {
        await using var context = db.NewContext();
        return await context.PaiResults.OrderBy(r => r.Id).Select(r => r.ComputedAt).ToListAsync();
    }
}
