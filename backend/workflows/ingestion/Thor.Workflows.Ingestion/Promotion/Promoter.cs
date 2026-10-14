using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Npgsql;
using Thor.DataLayer.Data;
using Thor.DataLayer.Models.Tenants;
using Thor.Workflows.Ingestion.Batching;
using Thor.Workflows.Ingestion.EdgeGate;

namespace Thor.Workflows.Ingestion.Promotion;

public sealed record PromotionResult(int Inserted, int Updated);

public sealed record IdentityPromotionResult(int Inserted, int Updated, int Deactivated);

/// <summary>
/// Promotes staged rows for one scan manifest into their canonical tables — a set-based,
/// content-hash-diffed upsert, a key-range batch at a time. Each batch is its own transaction:
/// it upserts the range, records one <see cref="IngestChangeEvent"/> per actual insert/update,
/// replaces the <see cref="EntityAlias"/> rows the edge gate depends on (§7.1), and deletes the
/// range's staging rows, so a retry resumes from whatever staging is left.
///
/// <para>Each batch also records every staged row — changed or not — in <see cref="ScanSeenEntity"/>.
/// That ledger is the only record of what a scan delivered, and is what lets the reconcile workflow
/// tell an entity the source stopped sending from one that simply has not changed. It reads
/// <c>deduped</c>, not the upsert's <c>RETURNING</c> set, because that set holds only changed rows.</para>
/// </summary>
public sealed class Promoter(ILogger<Promoter> logger, IngestionBatchOptions? batchOptions = null)
{
    private static readonly Guid UnclassifiedAccountTypeId = WellKnownAccountTypes.Unclassified;

    private readonly IngestionBatchOptions _batch = batchOptions ?? IngestionBatchOptions.Default;
    private readonly EdgeRefDeltaComputer _edgeRefDeltas = new();

    /// <summary>The <c>identity.source</c> label for rows promoted from an HR feed.</summary>
    internal const string HrFeedIdentitySource = "hr_feed";

    private sealed record PromotableEntity(string EntityType, string StagingTable, string CanonicalTable, string InsertSql, Func<NpgsqlParameter[]> ExtraParameters);

    private static readonly PromotableEntity Accounts = new("account", "staging_account", "tenant.account", """
        INSERT INTO tenant.account (
            id, source_id, connector_type, native_id, account_kind, is_human, display_name,
            sam_account_name, upn, email, domain_name, filer_name, native_account_id,
            is_deleted, is_disabled, account_type_id, raw_attributes, content_hash,
            hash_version, created_at, updated_at
        )
        SELECT gen_random_uuid(), source_id, connector_type, native_id, account_kind, is_human,
               display_name, sam_account_name, upn, email, domain_name, filer_name,
               native_account_id, is_deleted, is_disabled, @unclassified_account_type_id,
               raw_attributes, content_hash, 0, now(), now()
        FROM deduped
        ON CONFLICT (source_id, native_id) DO UPDATE SET
            account_kind = excluded.account_kind,
            is_human = excluded.is_human,
            display_name = excluded.display_name,
            sam_account_name = excluded.sam_account_name,
            upn = excluded.upn,
            email = excluded.email,
            domain_name = excluded.domain_name,
            filer_name = excluded.filer_name,
            native_account_id = excluded.native_account_id,
            is_deleted = excluded.is_deleted,
            is_disabled = excluded.is_disabled,
            raw_attributes = excluded.raw_attributes,
            content_hash = excluded.content_hash,
            updated_at = now()
        WHERE tenant.account.content_hash IS DISTINCT FROM excluded.content_hash
        RETURNING id, source_id, connector_type, raw_attributes, (xmax = 0) AS inserted
        """,
        () => [new NpgsqlParameter("unclassified_account_type_id", UnclassifiedAccountTypeId)]);

    private static readonly PromotableEntity Groups = new("grp", "staging_grp", "tenant.grp", """
        INSERT INTO tenant.grp (
            id, source_id, connector_type, native_id, group_class, display_name, email,
            domain_name, is_large_group, is_deleted, raw_attributes, content_hash,
            hash_version, created_at, updated_at
        )
        SELECT gen_random_uuid(), source_id, connector_type, native_id, group_class,
               display_name, email, domain_name, is_large_group, is_deleted,
               raw_attributes, content_hash, 0, now(), now()
        FROM deduped
        ON CONFLICT (source_id, native_id) DO UPDATE SET
            group_class = excluded.group_class,
            display_name = excluded.display_name,
            email = excluded.email,
            domain_name = excluded.domain_name,
            is_large_group = excluded.is_large_group,
            is_deleted = excluded.is_deleted,
            raw_attributes = excluded.raw_attributes,
            content_hash = excluded.content_hash,
            updated_at = now()
        WHERE tenant.grp.content_hash IS DISTINCT FROM excluded.content_hash
        RETURNING id, source_id, connector_type, raw_attributes, (xmax = 0) AS inserted
        """,
        () => []);

    // is_deleted is a literal FALSE for assets and entitlements: their staging tables carry no such
    // column, since no connector reports their deletion. Only the reconcile workflow sets it, and it
    // blanks content_hash when it does, so a deleted row the source sends again passes the hash guard
    // below and this resets it — the same revival account and grp get through their staged flag.
    private static readonly PromotableEntity Assets = new("asset", "staging_asset", "tenant.asset", """
        INSERT INTO tenant.asset (
            id, source_id, connector_type, native_id, asset_type, display_name, full_path,
            filer_name, file_size, file_count, broken_acl, is_protected, parent_asset_id,
            is_deleted, raw_attributes, content_hash, hash_version, created_at, updated_at
        )
        SELECT gen_random_uuid(), source_id, connector_type, native_id, asset_type,
               display_name, full_path, filer_name, file_size, file_count, broken_acl,
               is_protected, NULL, FALSE, raw_attributes, content_hash, 0, now(), now()
        FROM deduped
        ON CONFLICT (source_id, native_id) DO UPDATE SET
            asset_type = excluded.asset_type,
            display_name = excluded.display_name,
            full_path = excluded.full_path,
            filer_name = excluded.filer_name,
            file_size = excluded.file_size,
            file_count = excluded.file_count,
            broken_acl = excluded.broken_acl,
            is_protected = excluded.is_protected,
            is_deleted = FALSE,
            raw_attributes = excluded.raw_attributes,
            content_hash = excluded.content_hash,
            updated_at = now()
        WHERE tenant.asset.content_hash IS DISTINCT FROM excluded.content_hash
        RETURNING id, source_id, connector_type, raw_attributes, (xmax = 0) AS inserted
        """,
        () => []);

    private static readonly PromotableEntity Entitlements = new("entitlement", "staging_entitlement", "tenant.entitlement", """
        INSERT INTO tenant.entitlement (
            id, source_id, connector_type, native_id, entitlement_type, name, description,
            is_admin, scope, instance_name, sudo_path, sudo_host, is_deleted, raw_attributes,
            content_hash, hash_version, created_at, updated_at
        )
        SELECT gen_random_uuid(), source_id, connector_type, native_id, entitlement_type,
               name, description, is_admin, scope, instance_name, sudo_path, sudo_host,
               FALSE, raw_attributes, content_hash, 0, now(), now()
        FROM deduped
        ON CONFLICT (source_id, native_id) DO UPDATE SET
            entitlement_type = excluded.entitlement_type,
            name = excluded.name,
            description = excluded.description,
            is_admin = excluded.is_admin,
            scope = excluded.scope,
            instance_name = excluded.instance_name,
            sudo_path = excluded.sudo_path,
            sudo_host = excluded.sudo_host,
            is_deleted = FALSE,
            raw_attributes = excluded.raw_attributes,
            content_hash = excluded.content_hash,
            updated_at = now()
        WHERE tenant.entitlement.content_hash IS DISTINCT FROM excluded.content_hash
        RETURNING id, source_id, connector_type, raw_attributes, (xmax = 0) AS inserted
        """,
        () => []);

    /// <param name="computeEdgeRefDeltas">Records each batch's added/removed edge refs before its upsert overwrites them; off on the initial scan.</param>
    public Task<PromotionResult> PromoteAccountsAsync(TenantDbContext db, Guid scanManifestId, Guid scanId, bool computeEdgeRefDeltas = false, CancellationToken cancellationToken = default) =>
        PromoteAsync(db, Accounts, scanManifestId, scanId, computeEdgeRefDeltas, cancellationToken);

    public Task<PromotionResult> PromoteGroupsAsync(TenantDbContext db, Guid scanManifestId, Guid scanId, bool computeEdgeRefDeltas = false, CancellationToken cancellationToken = default) =>
        PromoteAsync(db, Groups, scanManifestId, scanId, computeEdgeRefDeltas, cancellationToken);

    public Task<PromotionResult> PromoteAssetsAsync(TenantDbContext db, Guid scanManifestId, Guid scanId, bool computeEdgeRefDeltas = false, CancellationToken cancellationToken = default) =>
        PromoteAsync(db, Assets, scanManifestId, scanId, computeEdgeRefDeltas, cancellationToken);

    public Task<PromotionResult> PromoteEntitlementsAsync(TenantDbContext db, Guid scanManifestId, Guid scanId, bool computeEdgeRefDeltas = false, CancellationToken cancellationToken = default) =>
        PromoteAsync(db, Entitlements, scanManifestId, scanId, computeEdgeRefDeltas, cancellationToken);

    private async Task<PromotionResult> PromoteAsync(
        TenantDbContext db, PromotableEntity entity, Guid scanManifestId, Guid scanId, bool computeEdgeRefDeltas, CancellationToken cancellationToken)
    {
        var batcher = new StagingKeyBatcher($"tenant.{entity.StagingTable}", "native_id");
        var inserted = 0;
        var updated = 0;
        var batchNumber = 0;
        StagingKey? after = null;

        while (await batcher.NextRangeAsync(db, scanManifestId, after, _batch.Rows, cancellationToken) is { } range)
        {
            var timer = Stopwatch.StartNew();
            var (batchInserted, batchUpdated) = await TransientRetry.RunAsync(
                () => PromoteBatchAsync(db, entity, range, scanManifestId, scanId, computeEdgeRefDeltas, cancellationToken),
                logger, cancellationToken);

            inserted += batchInserted;
            updated += batchUpdated;
            after = range.Through;
            batchNumber++;

            logger.LogInformation(
                "Promoted {EntityType} batch {Batch} for scan manifest {ScanManifestId}: {Inserted} inserted, {Updated} updated in {ElapsedMs} ms",
                entity.EntityType, batchNumber, scanManifestId, batchInserted, batchUpdated, timer.ElapsedMilliseconds);
        }

        logger.LogInformation(
            "Promoted {EntityType}(s) for scan manifest {ScanManifestId}: {Inserted} inserted, {Updated} updated across {Batches} batch(es)",
            entity.EntityType, scanManifestId, inserted, updated, batchNumber);

        return new PromotionResult(inserted, updated);
    }

    private async Task<(int Inserted, int Updated)> PromoteBatchAsync(
        TenantDbContext db, PromotableEntity entity, StagingKeyRange range, Guid scanManifestId, Guid scanId, bool computeEdgeRefDeltas, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        if (computeEdgeRefDeltas)
        {
            await _edgeRefDeltas.ComputeRangeAsync(db, scanManifestId, entity.CanonicalTable, entity.EntityType, range, cancellationToken);
        }

        var counts = await QueryBatchAsync(db, BatchStatement(entity, range),
            [new NpgsqlParameter("scan_manifest_id", scanManifestId), new NpgsqlParameter("scan_id", scanId), .. entity.ExtraParameters(), .. range.CreateParameters()],
            cancellationToken);

        // stagingTable is an internal constant (never external input) — table names can't be bound as parameters.
        await SqlExec.ExecAsync(db,
            $"DELETE FROM tenant.{entity.StagingTable} WHERE scan_manifest_id = @scan_manifest_id AND {range.Predicate("native_id")}",
            cancellationToken,
            [new NpgsqlParameter("scan_manifest_id", scanManifestId), .. range.CreateParameters()]);

        await transaction.CommitAsync(cancellationToken);
        return counts;
    }

    private static string BatchStatement(PromotableEntity entity, StagingKeyRange range) => $"""
        WITH deduped AS (
            SELECT DISTINCT ON (source_id, native_id) *
            FROM tenant.{entity.StagingTable}
            WHERE scan_manifest_id = @scan_manifest_id AND {range.Predicate("native_id")}
            ORDER BY source_id, native_id, received_at DESC, batch_seq DESC
        ),
        up AS (
            {entity.InsertSql}
        ),
        change_event AS (
            INSERT INTO tenant.ingest_change_event (id, scan_manifest_id, scan_id, entity_type, entity_id, change_type, occurred_at)
            SELECT gen_random_uuid(), @scan_manifest_id, @scan_id, '{entity.EntityType}', id,
                   CASE WHEN inserted THEN 'inserted' ELSE 'updated' END, now()
            FROM up
            RETURNING 1
        ),
        old_alias AS (
            DELETE FROM tenant.entity_alias
            WHERE entity_type = '{entity.EntityType}' AND entity_id IN (SELECT id FROM up)
            RETURNING 1
        ),
        new_alias AS (
            INSERT INTO tenant.entity_alias (id, entity_type, entity_id, source_id, connector_type, alias_key, created_at)
            SELECT gen_random_uuid(), '{entity.EntityType}', up.id, up.source_id, up.connector_type, k.alias_key, now()
            FROM up
            CROSS JOIN LATERAL jsonb_array_elements_text(COALESCE(up.raw_attributes::jsonb->'AliasKeys', '[]'::jsonb)) AS k(alias_key)
            RETURNING 1
        ),
        seen AS (
            INSERT INTO tenant.scan_seen_entity (scan_id, source_id, entity_type, native_id)
            SELECT @scan_id, source_id, '{entity.EntityType}', native_id
            FROM deduped
            ON CONFLICT DO NOTHING
            RETURNING 1
        )
        SELECT count(*) FILTER (WHERE inserted)::int, count(*) FILTER (WHERE NOT inserted)::int
        FROM up
        """;

    private static async Task<(int Inserted, int Updated)> QueryBatchAsync(
        TenantDbContext db, string sql, NpgsqlParameter[] parameters, CancellationToken cancellationToken)
    {
        var rows = await SqlExec.QueryAsync(db, sql, r => (r.GetInt32(0), r.GetInt32(1)), cancellationToken, parameters);
        return rows[0];
    }

    /// <summary>
    /// Promotes staged HR-feed people into <c>tenant.identity</c>, treating each source's upload
    /// as a full snapshot: the hash-diffed upsert (re)activates everyone present, then anyone of
    /// that source missing from it is deactivated. Deactivation is scoped to sources that staged
    /// at least one row in this manifest, so a manifest carrying only the connector's raw-feed
    /// companion file — or an HR file that failed extraction — never deactivates anyone.
    ///
    /// Not routed through <see cref="PromoteAsync"/>: identities have no connector type and no
    /// alias keys. Every statement is batched, but all batches share one transaction — the
    /// deactivation reads the whole staged set, so it can't be split from the staging delete.
    /// </summary>
    public Task<IdentityPromotionResult> PromoteIdentitiesAsync(TenantDbContext db, Guid scanManifestId, Guid scanId, CancellationToken cancellationToken = default) =>
        TransientRetry.RunAsync(() => PromoteIdentitiesOnceAsync(db, scanManifestId, scanId, cancellationToken), logger, cancellationToken);

    private async Task<IdentityPromotionResult> PromoteIdentitiesOnceAsync(TenantDbContext db, Guid scanManifestId, Guid scanId, CancellationToken cancellationToken)
    {
        var batcher = new StagingKeyBatcher("tenant.staging_identity", "hr_employee_id");

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        var inserted = 0;
        var updated = 0;
        StagingKey? after = null;
        while (await batcher.NextRangeAsync(db, scanManifestId, after, _batch.Rows, cancellationToken) is { } range)
        {
            var rows = await SqlExec.QueryAsync(db, IdentityUpsertStatement(range), r => (r.GetInt32(0), r.GetInt32(1)), cancellationToken,
                [new NpgsqlParameter("scan_manifest_id", scanManifestId), new NpgsqlParameter("scan_id", scanId), new NpgsqlParameter("source", HrFeedIdentitySource), .. range.CreateParameters()]);
            inserted += rows[0].Item1;
            updated += rows[0].Item2;
            after = range.Through;
        }

        var sourceIds = await SqlExec.QueryAsync(db,
            "SELECT DISTINCT source_id FROM tenant.staging_identity WHERE scan_manifest_id = @scan_manifest_id",
            r => r.GetGuid(0), cancellationToken, new NpgsqlParameter("scan_manifest_id", scanManifestId));

        var deactivated = 0;
        if (sourceIds.Count > 0)
        {
            int batchDeactivated;
            do
            {
                var rows = await SqlExec.QueryAsync(db, IdentityDeactivateStatement, r => r.GetInt32(0), cancellationToken,
                    new NpgsqlParameter("scan_manifest_id", scanManifestId),
                    new NpgsqlParameter("scan_id", scanId),
                    new NpgsqlParameter("source_ids", sourceIds.ToArray()),
                    new NpgsqlParameter("batch_rows", _batch.Rows));
                batchDeactivated = rows[0];
                deactivated += batchDeactivated;
            }
            while (batchDeactivated > 0);
        }

        // Last: the deactivation above reads these rows.
        await SqlExec.DeleteInBatchesAsync(db, "tenant.staging_identity", "scan_manifest_id = @scan_manifest_id", _batch.Rows,
            cancellationToken, new NpgsqlParameter("scan_manifest_id", scanManifestId));

        await transaction.CommitAsync(cancellationToken);

        logger.LogInformation(
            "Promoted identity(s) for scan manifest {ScanManifestId}: {Inserted} inserted, {Updated} updated, {Deactivated} deactivated",
            scanManifestId, inserted, updated, deactivated);

        return new IdentityPromotionResult(inserted, updated, deactivated);
    }

    private static string IdentityUpsertStatement(StagingKeyRange range) => $"""
        WITH deduped AS (
            SELECT DISTINCT ON (source_id, hr_employee_id) *
            FROM tenant.staging_identity
            WHERE scan_manifest_id = @scan_manifest_id AND {range.Predicate("hr_employee_id")}
            ORDER BY source_id, hr_employee_id, received_at DESC, batch_seq DESC
        ),
        up AS (
            INSERT INTO tenant.identity (
                id, source, source_id, hr_employee_id, display_name, email, given_name, surname,
                department, title, business_unit, sam_account_name, upn, manager_employee_id,
                manager_name, ad_match_field, ad_match_value, marked_to_retire, raw_attributes,
                is_active, content_hash, created_at, updated_at
            )
            SELECT gen_random_uuid(), @source, source_id, hr_employee_id, display_name, email,
                   given_name, surname, department, title, business_unit, sam_account_name, upn,
                   manager_employee_id, manager_name, ad_match_field, ad_match_value,
                   marked_to_retire, raw_attributes, true, content_hash, now(), now()
            FROM deduped
            ON CONFLICT (source_id, hr_employee_id) DO UPDATE SET
                display_name = excluded.display_name,
                email = excluded.email,
                given_name = excluded.given_name,
                surname = excluded.surname,
                department = excluded.department,
                title = excluded.title,
                business_unit = excluded.business_unit,
                sam_account_name = excluded.sam_account_name,
                upn = excluded.upn,
                manager_employee_id = excluded.manager_employee_id,
                manager_name = excluded.manager_name,
                ad_match_field = excluded.ad_match_field,
                ad_match_value = excluded.ad_match_value,
                marked_to_retire = excluded.marked_to_retire,
                raw_attributes = excluded.raw_attributes,
                is_active = true,
                content_hash = excluded.content_hash,
                updated_at = now()
            WHERE tenant.identity.content_hash IS DISTINCT FROM excluded.content_hash
               OR NOT tenant.identity.is_active
            RETURNING id, (xmax = 0) AS inserted
        ),
        change_event AS (
            INSERT INTO tenant.ingest_change_event (id, scan_manifest_id, scan_id, entity_type, entity_id, change_type, occurred_at)
            SELECT gen_random_uuid(), @scan_manifest_id, @scan_id, 'identity', id,
                   CASE WHEN inserted THEN 'inserted' ELSE 'updated' END, now()
            FROM up
            RETURNING 1
        )
        SELECT count(*) FILTER (WHERE inserted)::int, count(*) FILTER (WHERE NOT inserted)::int
        FROM up
        """;

    // A deactivation is a field change on a row that still exists, so it's recorded as
    // "updated" — "deleted" means the row itself is gone (see GraphLoadWriteSet's edge handling).
    // Deactivated rows leave the is_active predicate, so repeating the statement drains the set.
    private const string IdentityDeactivateStatement = """
        WITH target AS (
            SELECT i.id
            FROM tenant.identity i
            WHERE i.is_active
              AND i.source_id = ANY(@source_ids)
              AND NOT EXISTS (
                  SELECT 1 FROM tenant.staging_identity s
                  WHERE s.scan_manifest_id = @scan_manifest_id
                    AND s.source_id = i.source_id
                    AND s.hr_employee_id = i.hr_employee_id)
            ORDER BY i.id
            LIMIT @batch_rows
        ),
        deactivated AS (
            UPDATE tenant.identity i
            SET is_active = false, updated_at = now()
            FROM target t
            WHERE i.id = t.id
            RETURNING i.id
        ),
        change_event AS (
            INSERT INTO tenant.ingest_change_event (id, scan_manifest_id, scan_id, entity_type, entity_id, change_type, occurred_at)
            SELECT gen_random_uuid(), @scan_manifest_id, @scan_id, 'identity', id, 'updated', now()
            FROM deactivated
            RETURNING 1
        )
        SELECT count(*)::int FROM deactivated
        """;
}
