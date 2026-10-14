using Microsoft.EntityFrameworkCore;
using Thor.DataLayer.Data;
using Thor.DataLayer.Models.Tenants;

namespace Thor.Workflows.GraphLoad.Persistence;

/// <summary>
/// What a scan's graph load writes and deletes, read from Postgres as it stands now. "Changed" means
/// named by an <c>ingest_change_event</c> of the scan that either comes from one of
/// <paramref name="scanManifestIds"/> — the scan's manifests whose ingestion completed — or belongs to
/// no manifest, which is the reconcile workflow's deletions (it runs before this load). The row's
/// current state decides what happens to it: a live row is written, a soft-deleted one is deleted.
/// Reading current state rather than a snapshot taken at ingestion means a later scan that changed the
/// same rows first is never undone by this one.
///
/// <para>The vertex set is the changed vertices <em>plus every endpoint of a changed edge</em> — an
/// edge into a group from an earlier scan writes that group again. <c>mergeE</c> fails a whole request
/// over one missing endpoint, and re-upserting an endpoint that already exists is idempotent and cheap,
/// so this is simpler and safer than asking Neptune which endpoints it lacks. An edge is written only
/// when both its endpoints are live and of a kind the graph holds; one into a deleted, missing or
/// entitlement vertex is not written at all.</para>
///
/// <para>Shaped for Postgres at scan scale, where every set below can run to millions of rows: a vertex
/// set is a single <c>IN</c> over a <c>UNION ALL</c>, which the planner turns into a join that spills to
/// disk if it must — three <c>IN</c>s joined by <c>OR</c> would instead have to fit each subquery's hash
/// table in <c>work_mem</c> or fall back to re-running it per row. An edge's endpoint checks are
/// primary-key probes, two per changed edge.</para>
/// </summary>
internal sealed class GraphLoadWriteSet(TenantDbContext db, Guid scanId, IReadOnlyCollection<Guid> scanManifestIds)
{
    private readonly List<Guid> _scanManifestIds = [.. scanManifestIds];

    /// <summary>Live vertex ids of <paramref name="kind"/> to write, in a stable order.</summary>
    public IQueryable<Guid> VertexIds(string kind)
    {
        var wanted = Changed(kind)
            .Concat(ChangedLiveEdges().Where(e => e.FromType == kind).Select(e => e.FromId))
            .Concat(ChangedLiveEdges().Where(e => e.ToType == kind).Select(e => e.ToId));

        return kind switch
        {
            GraphLoadKinds.Account => db.Accounts.Where(a => !a.IsDeleted && wanted.Contains(a.Id)).OrderBy(a => a.Id).Select(a => a.Id),
            GraphLoadKinds.Group => db.Grps.Where(g => !g.IsDeleted && wanted.Contains(g.Id)).OrderBy(g => g.Id).Select(g => g.Id),
            GraphLoadKinds.Asset => db.Assets.Where(a => !a.IsDeleted && wanted.Contains(a.Id)).OrderBy(a => a.Id).Select(a => a.Id),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Not a vertex kind."),
        };
    }

    /// <summary>
    /// Live edge ids to write, ordered by target: the edges into one vertex — a large group's members —
    /// then land in as few chunks as possible, and concurrent writes to one vertex are what conflicts
    /// on Neptune.
    /// </summary>
    public IQueryable<Guid> EdgeIds() =>
        ChangedLiveEdges()
            .Where(e => (e.FromType == GraphLoadKinds.Account && db.Accounts.Any(a => a.Id == e.FromId && !a.IsDeleted))
                || (e.FromType == GraphLoadKinds.Group && db.Grps.Any(g => g.Id == e.FromId && !g.IsDeleted))
                || (e.FromType == GraphLoadKinds.Asset && db.Assets.Any(a => a.Id == e.FromId && !a.IsDeleted)))
            .Where(e => (e.ToType == GraphLoadKinds.Account && db.Accounts.Any(a => a.Id == e.ToId && !a.IsDeleted))
                || (e.ToType == GraphLoadKinds.Group && db.Grps.Any(g => g.Id == e.ToId && !g.IsDeleted))
                || (e.ToType == GraphLoadKinds.Asset && db.Assets.Any(a => a.Id == e.ToId && !a.IsDeleted)))
            .OrderBy(e => e.ToId).ThenBy(e => e.Id).Select(e => e.Id);

    /// <summary>Changed accounts, groups and assets that are now soft-deleted.</summary>
    public async Task<List<(string Kind, Guid Id)>> DeletedVerticesAsync(CancellationToken cancellationToken)
    {
        var changedAccounts = Changed(GraphLoadKinds.Account);
        var changedGroups = Changed(GraphLoadKinds.Group);
        var changedAssets = Changed(GraphLoadKinds.Asset);

        var accounts = await db.Accounts.Where(a => a.IsDeleted && changedAccounts.Contains(a.Id)).Select(a => a.Id).ToListAsync(cancellationToken);
        var groups = await db.Grps.Where(g => g.IsDeleted && changedGroups.Contains(g.Id)).Select(g => g.Id).ToListAsync(cancellationToken);
        var assets = await db.Assets.Where(a => a.IsDeleted && changedAssets.Contains(a.Id)).Select(a => a.Id).ToListAsync(cancellationToken);

        return
        [
            .. accounts.Select(id => (GraphLoadKinds.Account, id)),
            .. groups.Select(id => (GraphLoadKinds.Group, id)),
            .. assets.Select(id => (GraphLoadKinds.Asset, id)),
        ];
    }

    /// <summary>
    /// Changed edges that are now soft-deleted — a membership removed, as the edge gate records it —
    /// whose two endpoints are both still live vertices. An edge with a deleted endpoint goes with that
    /// vertex's <c>drop()</c>, and one into an entitlement or a missing row was never written; the
    /// reconcile workflow soft-deletes every edge of each entity it deletes, so dropping those one by
    /// one would be most of this step's work for nothing.
    /// </summary>
    public Task<List<Edge>> DeletedEdgesAsync(CancellationToken cancellationToken)
    {
        var changedEdges = Changed(GraphLoadKinds.Edge);
        return db.Edges.AsNoTracking()
            .Where(e => e.IsDeleted && changedEdges.Contains(e.Id))
            .Where(e => (e.FromType == GraphLoadKinds.Account && db.Accounts.Any(a => a.Id == e.FromId && !a.IsDeleted))
                || (e.FromType == GraphLoadKinds.Group && db.Grps.Any(g => g.Id == e.FromId && !g.IsDeleted))
                || (e.FromType == GraphLoadKinds.Asset && db.Assets.Any(a => a.Id == e.FromId && !a.IsDeleted)))
            .Where(e => (e.ToType == GraphLoadKinds.Account && db.Accounts.Any(a => a.Id == e.ToId && !a.IsDeleted))
                || (e.ToType == GraphLoadKinds.Group && db.Grps.Any(g => g.Id == e.ToId && !g.IsDeleted))
                || (e.ToType == GraphLoadKinds.Asset && db.Assets.Any(a => a.Id == e.ToId && !a.IsDeleted)))
            .ToListAsync(cancellationToken);
    }

    /// <summary>A manifest-less event is the reconcile workflow's, and always counts — see the class summary.</summary>
    private IQueryable<Guid> Changed(string entityType) =>
        db.IngestChangeEvents
            .Where(e => e.ScanId == scanId && e.EntityType == entityType
                && (e.ScanManifestId == null || _scanManifestIds.Contains(e.ScanManifestId.Value)))
            .Select(e => e.EntityId);

    /// <summary>Changed edges that are not soft-deleted, whatever their endpoints.</summary>
    private IQueryable<Edge> ChangedLiveEdges()
    {
        var changedEdges = Changed(GraphLoadKinds.Edge);
        return db.Edges.Where(e => !e.IsDeleted && changedEdges.Contains(e.Id));
    }
}
