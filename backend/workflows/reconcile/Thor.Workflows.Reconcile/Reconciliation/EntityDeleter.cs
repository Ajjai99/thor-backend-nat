using Npgsql;
using Thor.DataLayer.Data;
using Thor.Workflows.Reconcile.Persistence;

namespace Thor.Workflows.Reconcile.Reconciliation;

/// <summary>
/// Soft-deletes one window of a run's staged entities, with everything that makes a deletion take
/// effect — edges, aliases, parked refs, active owners — in one transaction: an entity and its edges
/// either both go or neither does, so the graph load never sees a deleted entity whose edges still stand.
///
/// <para>Every write carries <c>is_deleted = FALSE</c>, so a retried or redriven chunk changes nothing
/// twice and emits no second round of change events. The change events carry no manifest: the graph
/// load and PAI read them by scan, which is how the deletions reach Neptune and the access paths.</para>
/// </summary>
internal sealed class EntityDeleter(TenantDbContext db, Guid runId, Guid scanId)
{
    public async Task<(int Entities, int Edges)> DeleteWindowAsync(long fromSeq, long toSeq, CancellationToken cancellationToken)
    {
        var staged = await ReconcileSql.QueryAsync(db, """
            SELECT entity_type, entity_id FROM tenant.reconcile_pending
            WHERE run_id = @run AND seq > @from AND seq <= @to
            """,
            [new NpgsqlParameter("run", runId), new NpgsqlParameter("from", fromSeq), new NpgsqlParameter("to", toSeq)],
            reader => (Type: reader.GetString(0), Id: reader.GetGuid(1)),
            cancellationToken);

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        var deleted = new List<Guid>(staged.Count);
        foreach (var group in staged.GroupBy(s => s.Type))
        {
            var ids = await SoftDeleteEntitiesAsync(group.Key, [.. group.Select(s => s.Id)], cancellationToken);
            await ForgetAliasesAndParkedRefsAsync(group.Key, [.. ids], cancellationToken);
            await RetireOwnershipAsync(group.Key, [.. ids], cancellationToken);
            deleted.AddRange(ids);
        }

        var edges = deleted.Count > 0 ? await SoftDeleteEdgesAsync([.. deleted], cancellationToken) : 0;

        await transaction.CommitAsync(cancellationToken);
        return (deleted.Count, edges);
    }

    /// <summary>
    /// The canonical rows and their <c>deleted</c> events.
    ///
    /// <para>Blanking <c>content_hash</c> stops the row becoming a permanent tombstone: an entity that comes
    /// back usually comes back byte-identical, and promotion only writes rows whose hash differs. <c>''</c>
    /// rather than NULL because the column is NOT NULL on most of these tables, and it can never equal a
    /// SHA-256 digest. Blanking <c>EdgeRefs</c> makes that revival worth having: edge deltas diff staged refs
    /// against canonical's, so stale refs would produce no delta and the revived entity would hold nothing.</para>
    ///
    /// <para>An entity a scan still being ingested has already seen is left alone. A newer scan of the
    /// source that started after this run's decisions knows better, and once it has promoted the row nothing
    /// would revive it; the newer scan's own run reconciles it instead.</para>
    /// </summary>
    private Task<List<Guid>> SoftDeleteEntitiesAsync(string type, Guid[] ids, CancellationToken cancellationToken) =>
        // type is one of EntityTypes — an internal constant and the table's name.
        ReconcileSql.QueryAsync(db, $"""
            WITH deleted AS (
                UPDATE tenant.{type} e
                SET is_deleted = TRUE,
                    content_hash = '',
                    raw_attributes = CASE
                        WHEN e.raw_attributes IS NULL THEN NULL
                        ELSE (e.raw_attributes::jsonb || jsonb_build_object('EdgeRefs', '[]'::jsonb))::text
                    END,
                    deletion_proposed_at = NULL,
                    deletion_proposed_scan_id = NULL,
                    updated_at = now()
                WHERE e.id = ANY(@ids) AND e.is_deleted = FALSE
                  AND NOT EXISTS (
                      SELECT 1
                      FROM tenant.scan sc
                      JOIN tenant.scan_seen_entity s
                        ON s.scan_id = sc.id AND s.source_id = e.source_id
                       AND s.entity_type = @entity_type AND s.native_id = e.native_id
                      WHERE sc.ingestion_completed_at IS NULL)
                RETURNING e.id
            ),
            events AS (
                INSERT INTO tenant.ingest_change_event (id, scan_manifest_id, scan_id, entity_type, entity_id, change_type, occurred_at)
                SELECT gen_random_uuid(), NULL, @scan, @entity_type, id, 'deleted', now() FROM deleted
                RETURNING 1
            )
            SELECT id FROM deleted
            """,
            [new NpgsqlParameter("ids", ids), new NpgsqlParameter("scan", scanId), new NpgsqlParameter("entity_type", type)],
            reader => reader.GetGuid(0),
            cancellationToken);

    /// <summary>
    /// Every edge into or out of the deleted entities, with a <c>deleted</c> event each — the events PAI
    /// seeds from, so the containers that lost members are recomputed.
    ///
    /// <para>Locked in id order in one statement. Chunks run in parallel and two of them can share an edge
    /// — an account in one, its group in the other — so taking the locks in a single global order is what
    /// keeps them from deadlocking. A chunk that waited re-checks <c>is_deleted</c> once it has the lock, so
    /// an edge the other chunk deleted is skipped: one event per edge.</para>
    /// </summary>
    private async Task<int> SoftDeleteEdgesAsync(Guid[] ids, CancellationToken cancellationToken)
    {
        var rows = await ReconcileSql.QueryAsync(db, """
            WITH target AS (
                SELECT e.id
                FROM tenant.edge e
                WHERE e.is_deleted = FALSE
                  AND e.id IN (
                      SELECT id FROM tenant.edge WHERE from_id = ANY(@ids)
                      UNION
                      SELECT id FROM tenant.edge WHERE to_id = ANY(@ids))
                ORDER BY e.id
                FOR UPDATE
            ),
            deleted AS (
                UPDATE tenant.edge e
                SET is_deleted = TRUE, updated_at = now()
                FROM target t
                WHERE e.id = t.id
                RETURNING e.id
            ),
            events AS (
                INSERT INTO tenant.ingest_change_event (id, scan_manifest_id, scan_id, entity_type, entity_id, change_type, occurred_at)
                SELECT gen_random_uuid(), NULL, @scan, 'edge', id, 'deleted', now() FROM deleted
                RETURNING 1
            )
            SELECT count(*)::int FROM deleted
            """,
            [new NpgsqlParameter("ids", ids), new NpgsqlParameter("scan", scanId)],
            reader => reader.GetInt32(0),
            cancellationToken);
        return rows[0];
    }

    /// <summary>
    /// The two indexes that would otherwise resurrect a deleted entity sideways: an alias left behind lets
    /// a reference resolve onto the tombstone, and a parked one-sided ref would later heal into a live edge
    /// from it — the edge gate only refreshes refs of owners it is re-processing, which a deleted entity
    /// never is.
    /// </summary>
    private async Task ForgetAliasesAndParkedRefsAsync(string type, Guid[] ids, CancellationToken cancellationToken)
    {
        if (ids.Length == 0)
        {
            return;
        }

        // entity_type leads the alias index's key, so it is filtered on even though the ids alone are unique.
        await ReconcileSql.ExecAsync(db, "DELETE FROM tenant.entity_alias WHERE entity_type = @entity_type AND entity_id = ANY(@ids)",
            [new NpgsqlParameter("entity_type", type), new NpgsqlParameter("ids", ids)], cancellationToken);
        await ReconcileSql.ExecAsync(db, "DELETE FROM tenant.one_sided_ref WHERE owner_id = ANY(@ids)",
            [new NpgsqlParameter("ids", ids)], cancellationToken);
    }

    /// <summary>
    /// Deactivates the deleted entities' owners, with an audit event each. Ownership never revisits an
    /// entity no manifest carries, so nothing else would: the owner would stay active on a tombstone, and
    /// still count as a <c>sibling_match</c> sibling's evidence. An entity that comes back is promoted with a
    /// change event, and its manifest's Ownership run finds it unassigned and proposes again.
    /// </summary>
    private async Task RetireOwnershipAsync(string type, Guid[] ids, CancellationToken cancellationToken)
    {
        if (ids.Length == 0)
        {
            return;
        }

        // entity_type and entity_id lead party_assignment's unique index.
        await ReconcileSql.ExecAsync(db, """
            WITH retired AS (
                UPDATE tenant.party_assignment pa
                SET is_active = FALSE
                WHERE pa.entity_type = @entity_type AND pa.entity_id = ANY(@ids) AND pa.is_active
                RETURNING pa.id, pa.identity_id
            )
            INSERT INTO tenant.party_assignment_event (id, assignment_id, event_type, previous_identity_id, new_identity_id, actor, campaign_id, run_id, occurred_at)
            SELECT gen_random_uuid(), id, @event_type, identity_id, NULL, @actor, NULL, @run, now() FROM retired
            """,
            [
                new NpgsqlParameter("entity_type", type), new NpgsqlParameter("ids", ids),
                new NpgsqlParameter("event_type", EntityDeletedEventType), new NpgsqlParameter("actor", ReconcileActor),
                new NpgsqlParameter("run", runId.ToString()),
            ],
            cancellationToken);
    }

    /// <summary><c>party_assignment_event.event_type</c> for an owner retired because its entity was deleted.</summary>
    internal const string EntityDeletedEventType = "entity_deleted";

    /// <summary><c>party_assignment_event.actor</c> for events this module writes.</summary>
    internal const string ReconcileActor = "reconcile_workflow";
}
