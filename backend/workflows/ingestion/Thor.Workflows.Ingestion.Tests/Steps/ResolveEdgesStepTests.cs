using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Thor.DataConnectionManager;
using Thor.DataLayer.Data;
using Thor.Workflows.Ingestion.Steps;
using Xunit;

namespace Thor.Workflows.Ingestion.Tests.Steps;

/// <summary>
/// Exercises <see cref="ResolveEdgesStep"/> against a real Postgres. It has no graph dependency to give
/// it, which is the point: the graph is the graph-load workflow's.
/// </summary>
public sealed class ResolveEdgesStepTests : ResolveEdgesDatabase
{
    private ResolveEdgesStep Step() => new(NullLoggerFactory.Instance, new FakeTenantConnectionManager(_ => Context));

    [Fact]
    public async Task RunAsync_ResolvesTheManifestsEdgesIntoPostgres_AndCompletesTheManifest()
    {
        var manifestId = await SeedManifestAsync();
        var groupId = await InsertGroupAsync(manifestId, "eng", aliasKey: "cn=eng,dc=test");
        var accountId = await InsertAccountAsync(manifestId, "dave", aliasKey: "cn=dave,dc=test",
            edgeRefs: [("MEMBER_OF", "out", "cn=eng,dc=test", "grp")]);

        await Step().RunAsync(Context, ScanId, manifestId);

        var edge = await Context.Edges.AsNoTracking().SingleAsync();
        Assert.Equal(("MEMBER_OF", "account", accountId, "grp", groupId), (edge.RelType, edge.FromType, edge.FromId, edge.ToType, edge.ToId));
        Assert.True(await Context.IngestChangeEvents.AnyAsync(e => e.ScanManifestId == manifestId && e.EntityType == "edge" && e.EntityId == edge.Id));

        var workflow = await Context.Workflows.AsNoTracking().SingleAsync(w => w.ScanManifestId == manifestId);
        Assert.Equal("completed", workflow.Status);
        Assert.NotNull(workflow.CompletedAt);
    }

    /// <summary>The target is another manifest's: resolution goes by alias across the scan, not within one manifest.</summary>
    [Fact]
    public async Task RunAsync_AnEdgeIntoASiblingManifestsVertex_IsResolved()
    {
        var siblingManifestId = await SeedManifestAsync();
        var manifestId = await SeedManifestAsync();
        var groupId = await InsertGroupAsync(siblingManifestId, "ops", aliasKey: "cn=ops,dc=test");
        await InsertAccountAsync(manifestId, "erin", aliasKey: "cn=erin,dc=test",
            edgeRefs: [("MEMBER_OF", "out", "cn=ops,dc=test", "grp")]);

        await Step().RunAsync(Context, ScanId, manifestId);

        Assert.Equal(groupId, (await Context.Edges.AsNoTracking().SingleAsync()).ToId);
    }

    [Fact]
    public async Task RunAsync_ThrowsWhenCancelled()
    {
        var manifestId = await SeedManifestAsync();
        await InsertAccountAsync(manifestId, "grace");
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Step().RunAsync(Context, ScanId, manifestId, runId: null, cts.Token));
    }

    private sealed class FakeTenantConnectionManager(Func<Guid, TenantDbContext> factory) : ITenantConnectionManager
    {
        public Task<Npgsql.NpgsqlConnection> GetValidatedConnectionAsync(Guid tenantId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<TenantDbContext> GetTenantDbContextAsync(Guid tenantId, CancellationToken cancellationToken = default) =>
            Task.FromResult(factory(tenantId));
    }
}
