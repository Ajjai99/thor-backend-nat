using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;
using Thor.DataLayer.Data;
using Thor.Workflows.Ingestion.Batching;

namespace Thor.Workflows.Ingestion.EdgeGate;

public sealed record EdgeResolutionResult(int Inserted, int Updated, int Removed);

/// <summary>
/// Connector-agnostic edge resolution (§7 of <c>ad_connector_ingestion.md</c>) — joins every
/// owner's <c>edge_refs</c> against every entity's <c>alias_keys</c> to form canonical edges.
/// account/grp/asset/entitlement are all treated uniformly as owner tables (see
/// <see cref="OwnerRefsSql"/>), even though only account/grp populate <c>edge_refs</c> today.
///
/// <see cref="ResolveFullAsync"/> (the initial scan) resolves every owner's *entire* edge_refs
/// footprint — nothing to diff against yet, so there's nothing narrower to do.
/// <see cref="ResolveIncrementalAsync"/> is delta-driven (the POC's "B7"): it resolves only the
/// individual refs a manifest actually added or removed (via <c>tenant.edge_ref_delta</c>,
/// populated by <see cref="EdgeRefDeltaComputer"/> before promotion), plus specific one-sided
/// refs that just healed — so a single membership change in a huge group costs proportional to
/// that one change, not to the group's whole membership. These are genuinely different shapes
/// (mirroring the POC's own separate <c>_resolve_initial</c>/<c>_resolve_manifest_b7</c>
/// functions), so they no longer share one internal resolve-to-staging method.
///
/// Both run as bounded chunks, each a self-contained transaction that stages its edges,
/// promotes them and clears its staging. A removal candidate only has to be a superset of what
/// should go — the both-sided verification is what decides — so judging candidates against one
/// chunk's staging rather than the whole run's is safe. A retry restarts the resolve; the
/// hash-diffed upsert makes already-promoted chunks no-ops.
/// </summary>
public sealed class EdgeResolver(OneSidedRefIndex oneSidedRefIndex, ILogger<EdgeResolver> logger, IngestionBatchOptions? batchOptions = null)
{
    private const string EmptyProps = "{}";

    private const string DeltaSliceSql = "(SELECT * FROM delta_work WHERE seq > @seq_start AND seq <= @seq_end)";

    private readonly IngestionBatchOptions _batch = batchOptions ?? IngestionBatchOptions.Default;

    private sealed record OwnerChunk(Guid LastOwnerId, int Inserted, int Updated, int Removed);

    /// <summary>Resolves edges for every account/grp/asset/entitlement — the initial (full) scan path.</summary>
    public async Task<EdgeResolutionResult> ResolveFullAsync(TenantDbContext db, Guid scanId, Guid scanManifestId, IReadOnlySet<string> twoSidedRelTypes, CancellationToken cancellationToken = default)
    {
        // Temp tables live as long as the session, so hold the connection open across every chunk transaction.
        await db.Database.OpenConnectionAsync(cancellationToken);
        try
        {
            await PrepareAsync(db, scanManifestId, cancellationToken);

            var chunk = 0;
            var inserted = 0;
            var updated = 0;
            var removed = 0;
            foreach (var (table, entityType) in OwnerRefsSql.OwnerTables)
            {
                Guid? after = null;
                while (true)
                {
                    var (current, cursor) = (chunk, after);
                    var result = await TransientRetry.RunAsync(
                        () => ResolveOwnerChunkAsync(db, scanId, scanManifestId, twoSidedRelTypes, table, entityType, cursor, cancellationToken),
                        logger, cancellationToken);
                    if (result is null)
                    {
                        break;
                    }

                    inserted += result.Inserted;
                    updated += result.Updated;
                    removed += result.Removed;
                    after = result.LastOwnerId;
                    chunk++;
                    logger.LogInformation(
                        "Edge gate (full) chunk {Chunk} over {EntityType} for scan {ScanId}: {Inserted} inserted, {Updated} updated, {Removed} removed",
                        current, entityType, scanId, result.Inserted, result.Updated, result.Removed);
                }
            }

            await DropWorkTablesAsync(db, cancellationToken);

            logger.LogInformation("Edge gate (full) for scan {ScanId}: {Inserted} inserted, {Updated} updated", scanId, inserted, updated);
            return new EdgeResolutionResult(inserted, updated, removed);
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }

    /// <summary>
    /// Resolves edges for the refs one manifest actually added/removed (§ B7), plus one-sided
    /// refs that just healed, including conservative both-sided removal.
    /// </summary>
    public async Task<EdgeResolutionResult> ResolveIncrementalAsync(TenantDbContext db, Guid scanId, Guid scanManifestId, IReadOnlySet<string> twoSidedRelTypes, CancellationToken cancellationToken = default)
    {
        await db.Database.OpenConnectionAsync(cancellationToken);
        try
        {
            await PrepareAsync(db, scanManifestId, cancellationToken);
            var changedOwners = await MaterializeChangedOwnersAsync(db, scanId, scanManifestId, cancellationToken);
            var deltaRefs = await MaterializeDeltaAsync(db, scanManifestId, cancellationToken);

            // Every owner's one-sided refs must be parked before any heal runs: a ref parked by one
            // changed owner may heal against another changed owner's alias.
            foreach (var (seqStart, seqEnd) in Slices(changedOwners, _batch.OwnersPerEdgeChunk))
            {
                await TransientRetry.RunAsync(async () =>
                {
                    await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
                    await FillSeedFromChangedOwnersAsync(db, seqStart, seqEnd, cancellationToken);
                    await oneSidedRefIndex.RefreshForSeedAsync(db, twoSidedRelTypes, cancellationToken);
                    await transaction.CommitAsync(cancellationToken);
                    return 0;
                }, logger, cancellationToken);
            }

            var inserted = 0;
            var updated = 0;
            foreach (var (seqStart, seqEnd) in Slices(deltaRefs, _batch.Rows))
            {
                var (chunkInserted, chunkUpdated) = await TransientRetry.RunAsync(async () =>
                {
                    await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
                    await InsertDeltaStagingRowsAsync(db, scanManifestId, seqStart, seqEnd, cancellationToken);
                    var counts = await PromoteStagedEdgesAsync(db, scanId, scanManifestId, cancellationToken);
                    await DeleteStagedEdgesAsync(db, scanManifestId, cancellationToken);
                    await transaction.CommitAsync(cancellationToken);
                    return counts;
                }, logger, cancellationToken);
                inserted += chunkInserted;
                updated += chunkUpdated;
            }

            foreach (var (seqStart, seqEnd) in Slices(changedOwners, _batch.OwnersPerEdgeChunk))
            {
                var (chunkInserted, chunkUpdated) = await TransientRetry.RunAsync(async () =>
                {
                    await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
                    await FillSeedFromChangedOwnersAsync(db, seqStart, seqEnd, cancellationToken);
                    await InsertHealStagingRowsAsync(db, scanManifestId, cancellationToken);
                    var counts = await PromoteStagedEdgesAsync(db, scanId, scanManifestId, cancellationToken);
                    await DeleteStagedEdgesAsync(db, scanManifestId, cancellationToken);
                    await transaction.CommitAsync(cancellationToken);
                    return counts;
                }, logger, cancellationToken);
                inserted += chunkInserted;
                updated += chunkUpdated;
            }

            var removed = await ApplyDeltaRemovalsAsync(db, scanId, scanManifestId, deltaRefs, cancellationToken);

            await DropWorkTablesAsync(db, cancellationToken);
            await SqlExec.DeleteInBatchesAsync(db, "tenant.edge_ref_delta", "scan_manifest_id = @scan_manifest_id", _batch.Rows,
                cancellationToken, new NpgsqlParameter("scan_manifest_id", scanManifestId));

            logger.LogInformation(
                "Edge gate (incremental) for scan {ScanId}: {Inserted} inserted, {Updated} updated, {Removed} removed",
                scanId, inserted, updated, removed);
            return new EdgeResolutionResult(inserted, updated, removed);
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }

    /// <summary>Creates the run's temp tables and clears staged edges a failed earlier attempt left behind.</summary>
    private async Task PrepareAsync(TenantDbContext db, Guid gateManifestId, CancellationToken cancellationToken)
    {
        await DropWorkTablesAsync(db, cancellationToken);
        await SqlExec.ExecAsync(db, "CREATE TEMP TABLE seed_owner (id uuid PRIMARY KEY, owner_type text NOT NULL)", cancellationToken);
        await SqlExec.ExecAsync(db,
            "CREATE TEMP TABLE cand_removal (id uuid PRIMARY KEY, from_id uuid NOT NULL, to_id uuid NOT NULL, rel_type text NOT NULL)",
            cancellationToken);

        await DeleteStagedEdgesAsync(db, gateManifestId, cancellationToken);
    }

    private static async Task DropWorkTablesAsync(TenantDbContext db, CancellationToken cancellationToken)
    {
        foreach (var table in new[] { "seed_owner", "seed_all", "delta_work", "cand_removal", "cand_ep", "cand_produced" })
        {
            await SqlExec.ExecAsync(db, $"DROP TABLE IF EXISTS {table}", cancellationToken);
        }
    }

    private Task DeleteStagedEdgesAsync(TenantDbContext db, Guid gateManifestId, CancellationToken cancellationToken) =>
        SqlExec.DeleteInBatchesAsync(db, "tenant.staging_edge", "scan_manifest_id = @gate_manifest_id", _batch.Rows,
            cancellationToken, new NpgsqlParameter("gate_manifest_id", gateManifestId));

    /// <summary>
    /// Seeds the next slice of one owner table after <paramref name="after"/>, then stages, promotes
    /// and removes for just that slice. Null once the table is exhausted.
    /// </summary>
    private async Task<OwnerChunk?> ResolveOwnerChunkAsync(
        TenantDbContext db, Guid scanId, Guid scanManifestId, IReadOnlySet<string> twoSidedRelTypes,
        string table, string entityType, Guid? after, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        await SqlExec.ExecAsync(db, "TRUNCATE seed_owner", cancellationToken);

        var afterClause = after is null ? "" : "WHERE id > @after";
        NpgsqlParameter[] fillParameters = after is { } afterId
            ? [new("rows", _batch.OwnersPerEdgeChunk), new("after", afterId)]
            : [new("rows", _batch.OwnersPerEdgeChunk)];
        await SqlExec.ExecAsync(db,
            $"INSERT INTO seed_owner (id, owner_type) SELECT id, '{entityType}' FROM {table} {afterClause} ORDER BY id LIMIT @rows",
            cancellationToken, fillParameters);

        var last = await SqlExec.QueryAsync(db, "SELECT id FROM seed_owner ORDER BY id DESC LIMIT 1", r => r.GetGuid(0), cancellationToken);
        if (last.Count == 0)
        {
            return null;
        }

        await oneSidedRefIndex.RefreshForSeedAsync(db, twoSidedRelTypes, cancellationToken);
        await InsertOwnerScopedStagingRowsAsync(db, scanManifestId, cancellationToken);
        var (inserted, updated) = await PromoteStagedEdgesAsync(db, scanId, scanManifestId, cancellationToken);
        var removed = await ApplyOwnerScopedRemovalsAsync(db, scanId, scanManifestId, cancellationToken);
        await DeleteStagedEdgesAsync(db, scanManifestId, cancellationToken);

        await transaction.CommitAsync(cancellationToken);
        return new OwnerChunk(last[0], inserted, updated, removed);
    }

    /// <summary>Owners whose content changed this manifest, numbered so they can be taken a slice at a time. Used only to scope one-sided-ref refresh and healing; B7 resolves edges from <c>edge_ref_delta</c>, not from this set.</summary>
    private static async Task<long> MaterializeChangedOwnersAsync(TenantDbContext db, Guid scanId, Guid scanManifestId, CancellationToken cancellationToken)
    {
        const string sql = """
            CREATE TEMP TABLE seed_all AS
            SELECT row_number() OVER () AS seq, s.id, s.owner_type
            FROM (
                SELECT DISTINCT entity_id AS id, entity_type AS owner_type
                FROM tenant.ingest_change_event
                WHERE scan_id = @scan_id AND scan_manifest_id = @scan_manifest_id
                  AND entity_type IN ('account','grp','asset','entitlement')
            ) s
            """;
        await SqlExec.ExecAsync(db, sql, cancellationToken,
            new NpgsqlParameter("scan_id", scanId),
            new NpgsqlParameter("scan_manifest_id", scanManifestId));
        await SqlExec.ExecAsync(db, "CREATE INDEX ON seed_all (seq)", cancellationToken);

        return await SqlExec.ScalarLongAsync(db, "SELECT COALESCE(max(seq), 0) FROM seed_all", cancellationToken);
    }

    /// <summary>This manifest's <c>edge_ref_delta</c> rows, numbered so a chunk can take a slice without rescanning the table.</summary>
    private static async Task<long> MaterializeDeltaAsync(TenantDbContext db, Guid scanManifestId, CancellationToken cancellationToken)
    {
        await SqlExec.ExecAsync(db,
            "CREATE TEMP TABLE delta_work AS SELECT row_number() OVER () AS seq, d.* FROM tenant.edge_ref_delta d WHERE d.scan_manifest_id = @scan_manifest_id",
            cancellationToken, new NpgsqlParameter("scan_manifest_id", scanManifestId));
        await SqlExec.ExecAsync(db, "CREATE INDEX ON delta_work (seq)", cancellationToken);

        return await SqlExec.ScalarLongAsync(db, "SELECT COALESCE(max(seq), 0) FROM delta_work", cancellationToken);
    }

    private static IEnumerable<(long Start, long End)> Slices(long total, int size)
    {
        for (long start = 0; start < total; start += size)
        {
            yield return (start, start + size);
        }
    }

    private static async Task FillSeedFromChangedOwnersAsync(TenantDbContext db, long seqStart, long seqEnd, CancellationToken cancellationToken)
    {
        await SqlExec.ExecAsync(db, "TRUNCATE seed_owner", cancellationToken);
        await SqlExec.ExecAsync(db,
            "INSERT INTO seed_owner (id, owner_type) SELECT id, owner_type FROM seed_all WHERE seq > @seq_start AND seq <= @seq_end",
            cancellationToken, new NpgsqlParameter("seq_start", seqStart), new NpgsqlParameter("seq_end", seqEnd));
    }

    /// <summary>Full resolve: every seeded owner's entire current edge_refs footprint, resolved against entity_alias.</summary>
    private static async Task InsertOwnerScopedStagingRowsAsync(TenantDbContext db, Guid gateManifestId, CancellationToken cancellationToken)
    {
        var sql = $"""
            INSERT INTO tenant.staging_edge (scan_manifest_id, batch_seq, from_id, from_type, to_id, to_type, rel_type, props, content_hash, received_at)
            WITH owner_refs AS (
                {OwnerRefsSql.OwnerRefsCte("seed_owner")}
            ),
            {OwnerRefsSql.MatchedCte},
            deduped AS (
                SELECT DISTINCT ON (from_id, to_id, rel_type)
                    from_id, from_type, to_id, to_type, rel_type, COALESCE(props, '{EmptyProps}') AS props
                FROM matched
                ORDER BY from_id, to_id, rel_type
            )
            SELECT @gate_manifest_id, 0, from_id, from_type, to_id, to_type, rel_type, props,
                   {OwnerRefsSql.HashExpr}, now()
            FROM deduped
            """;
        await SqlExec.ExecAsync(db, sql, cancellationToken, new NpgsqlParameter("gate_manifest_id", gateManifestId));
    }

    /// <summary>Incremental (B7) resolve: the refs added this manifest, one slice of <c>delta_work</c> at a time.</summary>
    private static async Task InsertDeltaStagingRowsAsync(TenantDbContext db, Guid scanManifestId, long seqStart, long seqEnd, CancellationToken cancellationToken)
    {
        var sql = $"""
            INSERT INTO tenant.staging_edge (scan_manifest_id, batch_seq, from_id, from_type, to_id, to_type, rel_type, props, content_hash, received_at)
            WITH owner_refs AS (
                {OwnerRefsSql.DeltaOwnerRefsCte("add", DeltaSliceSql)}
            ),
            {OwnerRefsSql.MatchedCte},
            deduped AS (
                SELECT DISTINCT ON (from_id, to_id, rel_type)
                    from_id, from_type, to_id, to_type, rel_type, COALESCE(props, '{EmptyProps}') AS props
                FROM matched
                ORDER BY from_id, to_id, rel_type
            )
            SELECT @scan_manifest_id, 0, from_id, from_type, to_id, to_type, rel_type, props,
                   {OwnerRefsSql.HashExpr}, now()
            FROM deduped
            """;
        await SqlExec.ExecAsync(db, sql, cancellationToken,
            new NpgsqlParameter("scan_manifest_id", scanManifestId),
            new NpgsqlParameter("seq_start", seqStart),
            new NpgsqlParameter("seq_end", seqEnd));
    }

    /// <summary>Incremental (B7) resolve: parked one-sided refs whose target is among the seeded changed owners.</summary>
    private static async Task InsertHealStagingRowsAsync(TenantDbContext db, Guid scanManifestId, CancellationToken cancellationToken)
    {
        var sql = $"""
            INSERT INTO tenant.staging_edge (scan_manifest_id, batch_seq, from_id, from_type, to_id, to_type, rel_type, props, content_hash, received_at)
            WITH {OwnerRefsSql.HealTargetAliasFromSeedCte("seed_owner")},
            {OwnerRefsSql.HealRefsCte},
            deduped AS (
                SELECT DISTINCT ON (from_id, to_id, rel_type)
                    from_id, from_type, to_id, to_type, rel_type, COALESCE(props, '{EmptyProps}') AS props
                FROM heal_refs
                ORDER BY from_id, to_id, rel_type
            )
            SELECT @scan_manifest_id, 0, from_id, from_type, to_id, to_type, rel_type, props,
                   {OwnerRefsSql.HashExpr}, now()
            FROM deduped
            """;
        await SqlExec.ExecAsync(db, sql, cancellationToken, new NpgsqlParameter("scan_manifest_id", scanManifestId));
    }

    /// <summary>Promotes the staged edges into tenant.edge (hash-diffed upsert) and emits one ingest_change_event per actual insert/update. Shared by both resolve paths — identical regardless of how staging_edge got populated.</summary>
    private static async Task<(int Inserted, int Updated)> PromoteStagedEdgesAsync(TenantDbContext db, Guid scanId, Guid gateManifestId, CancellationToken cancellationToken)
    {
        const string promoteSql = """
            WITH deduped AS (
                SELECT DISTINCT ON (from_id, to_id, rel_type)
                    from_id, from_type, to_id, to_type, rel_type, NULLIF(props, '{}') AS props, content_hash
                FROM tenant.staging_edge
                WHERE scan_manifest_id = @gate_manifest_id
                ORDER BY from_id, to_id, rel_type
            ),
            up AS (
                INSERT INTO tenant.edge (id, from_id, from_type, to_id, to_type, rel_type, props, content_hash, is_deleted, created_at, updated_at)
                SELECT gen_random_uuid(), from_id, from_type, to_id, to_type, rel_type, props, content_hash, FALSE, now(), now()
                FROM deduped
                ON CONFLICT (from_id, to_id, rel_type) DO UPDATE SET
                    from_type = excluded.from_type,
                    to_type = excluded.to_type,
                    props = excluded.props,
                    content_hash = excluded.content_hash,
                    is_deleted = FALSE,
                    updated_at = now()
                WHERE tenant.edge.content_hash IS DISTINCT FROM excluded.content_hash OR tenant.edge.is_deleted
                RETURNING id, (xmax = 0) AS is_insert
            ),
            ev AS (
                INSERT INTO tenant.ingest_change_event (id, scan_manifest_id, scan_id, entity_type, entity_id, change_type, occurred_at)
                SELECT gen_random_uuid(), @gate_manifest_id, @scan_id, 'edge', id,
                       CASE WHEN is_insert THEN 'inserted' ELSE 'updated' END, now()
                FROM up
                RETURNING 1
            )
            SELECT
                count(*) FILTER (WHERE is_insert)::int,
                count(*) FILTER (WHERE NOT is_insert)::int
            FROM up
            """;

        var counts = await SqlExec.QueryAsync(db, promoteSql, r => (r.GetInt32(0), r.GetInt32(1)), cancellationToken,
            new NpgsqlParameter("gate_manifest_id", gateManifestId),
            new NpgsqlParameter("scan_id", scanId));
        return counts[0];
    }

    /// <summary>
    /// Full resolve's removal candidates for one chunk: every live edge incident to a seeded owner
    /// that this chunk's staging didn't reproduce. Edges produced from the other side land here too;
    /// verification keeps them.
    /// </summary>
    private async Task<int> ApplyOwnerScopedRemovalsAsync(TenantDbContext db, Guid scanId, Guid gateManifestId, CancellationToken cancellationToken)
    {
        await SqlExec.ExecAsync(db, "TRUNCATE cand_removal", cancellationToken);

        const string candidateSql = """
            INSERT INTO cand_removal (id, from_id, to_id, rel_type)
            SELECT e.id, e.from_id, e.to_id, e.rel_type
            FROM tenant.edge e
            JOIN seed_owner c ON c.id = e.from_id
            WHERE e.is_deleted = FALSE
              AND NOT EXISTS (
                  SELECT 1 FROM tenant.staging_edge s
                  WHERE s.scan_manifest_id = @gate_manifest_id
                    AND s.from_id = e.from_id AND s.to_id = e.to_id AND s.rel_type = e.rel_type)
            UNION
            SELECT e.id, e.from_id, e.to_id, e.rel_type
            FROM tenant.edge e
            JOIN seed_owner c ON c.id = e.to_id
            WHERE e.is_deleted = FALSE
              AND NOT EXISTS (
                  SELECT 1 FROM tenant.staging_edge s
                  WHERE s.scan_manifest_id = @gate_manifest_id
                    AND s.from_id = e.from_id AND s.to_id = e.to_id AND s.rel_type = e.rel_type)
            """;
        await SqlExec.ExecAsync(db, candidateSql, cancellationToken, new NpgsqlParameter("gate_manifest_id", gateManifestId));

        return await VerifyAndApplyRemovalsAsync(db, scanId, gateManifestId, cancellationToken);
    }

    /// <summary>
    /// B7 removal candidates: only the specific edge tuples this manifest's removed refs used to
    /// produce, a slice of <c>delta_work</c> at a time. The candidates are not pre-filtered against
    /// the adds — verification re-derives from canonical, which already holds the new refs.
    /// </summary>
    private async Task<int> ApplyDeltaRemovalsAsync(TenantDbContext db, Guid scanId, Guid gateManifestId, long deltaRefs, CancellationToken cancellationToken)
    {
        await SqlExec.ExecAsync(db, "TRUNCATE cand_removal", cancellationToken);

        var candidateSql = $"""
            INSERT INTO cand_removal (id, from_id, to_id, rel_type)
            WITH owner_refs AS (
                {OwnerRefsSql.DeltaOwnerRefsCte("remove", DeltaSliceSql)}
            ),
            {OwnerRefsSql.MatchedCte}
            SELECT DISTINCT e.id, e.from_id, e.to_id, e.rel_type
            FROM matched m
            JOIN tenant.edge e ON e.from_id = m.from_id AND e.to_id = m.to_id AND e.rel_type = m.rel_type
            WHERE e.is_deleted = FALSE
            ON CONFLICT (id) DO NOTHING
            """;
        foreach (var (seqStart, seqEnd) in Slices(deltaRefs, _batch.Rows))
        {
            await SqlExec.ExecAsync(db, candidateSql, cancellationToken,
                new NpgsqlParameter("scan_manifest_id", gateManifestId),
                new NpgsqlParameter("seq_start", seqStart),
                new NpgsqlParameter("seq_end", seqEnd));
        }

        return await TransientRetry.RunAsync(async () =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            var removed = await VerifyAndApplyRemovalsAsync(db, scanId, gateManifestId, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return removed;
        }, logger, cancellationToken);
    }

    /// <summary>
    /// Conservative both-sided removal (§7.4), shared by both removal-candidate sources above: a
    /// candidate is only actually marked deleted if NEITHER of its endpoints' *current* refs still
    /// produce it — checked by re-deriving edges for just those candidates' endpoints (bounded),
    /// never for the whole graph. Works through <c>cand_removal</c> a slice at a time, inside the
    /// caller's transaction.
    /// </summary>
    private async Task<int> VerifyAndApplyRemovalsAsync(TenantDbContext db, Guid scanId, Guid gateManifestId, CancellationToken cancellationToken)
    {
        var removed = 0;
        Guid? after = null;
        while (await NextCandidateBoundAsync(db, after, _batch.Rows, cancellationToken) is { } through)
        {
            removed += await VerifyAndApplySliceAsync(db, scanId, gateManifestId, after, through, cancellationToken);
            after = through;
        }

        return removed;
    }

    private static async Task<int> VerifyAndApplySliceAsync(TenantDbContext db, Guid scanId, Guid gateManifestId, Guid? after, Guid through, CancellationToken cancellationToken)
    {
        await SqlExec.ExecAsync(db, "DROP TABLE IF EXISTS cand_ep", cancellationToken);
        await SqlExec.ExecAsync(db,
            $"CREATE TEMP TABLE cand_ep AS SELECT from_id AS id FROM cand_removal WHERE {IdRange("id", after)} UNION SELECT to_id FROM cand_removal WHERE {IdRange("id", after)}",
            cancellationToken, IdRangeParameters(after, through));

        await SqlExec.ExecAsync(db, "DROP TABLE IF EXISTS cand_produced", cancellationToken);
        var producedSql = $"""
            CREATE TEMP TABLE cand_produced AS
            WITH owner_refs AS (
                {OwnerRefsSql.OwnerRefsCte("cand_ep")}
            ),
            {OwnerRefsSql.MatchedCte}
            SELECT DISTINCT from_id, to_id, rel_type FROM matched
            """;
        await SqlExec.ExecAsync(db, producedSql, cancellationToken);

        var deleteSql = $"""
            WITH del AS (
                UPDATE tenant.edge e SET is_deleted = TRUE, updated_at = now()
                FROM cand_removal cr
                WHERE e.id = cr.id
                  AND {IdRange("cr.id", after)}
                  AND NOT EXISTS (
                      SELECT 1 FROM cand_produced p
                      WHERE p.from_id = cr.from_id AND p.to_id = cr.to_id AND p.rel_type = cr.rel_type)
                RETURNING e.id
            ),
            ev AS (
                INSERT INTO tenant.ingest_change_event (id, scan_manifest_id, scan_id, entity_type, entity_id, change_type, occurred_at)
                SELECT gen_random_uuid(), @gate_manifest_id, @scan_id, 'edge', id, 'deleted', now() FROM del
                RETURNING 1
            )
            SELECT count(*)::int FROM del
            """;
        var deleted = await SqlExec.QueryAsync(db, deleteSql, r => r.GetInt32(0), cancellationToken,
            [new NpgsqlParameter("gate_manifest_id", gateManifestId), new NpgsqlParameter("scan_id", scanId), .. IdRangeParameters(after, through)]);
        return deleted[0];
    }

    /// <summary>The id that ends the next slice of up to <paramref name="rows"/> candidates after <paramref name="after"/>; null when none remain.</summary>
    private static async Task<Guid?> NextCandidateBoundAsync(TenantDbContext db, Guid? after, int rows, CancellationToken cancellationToken)
    {
        var afterClause = after is null ? "" : "WHERE id > @after";

        var bound = await SqlExec.QueryAsync(db, $"SELECT id FROM cand_removal {afterClause} ORDER BY id OFFSET @skip LIMIT 1",
            r => r.GetGuid(0), cancellationToken, AfterParameters(after, new NpgsqlParameter("skip", (long)rows - 1)));
        if (bound.Count > 0)
        {
            return bound[0];
        }

        // Fewer than a full slice remains, so the slice is everything left.
        var tail = await SqlExec.QueryAsync(db, $"SELECT id FROM cand_removal {afterClause} ORDER BY id DESC LIMIT 1",
            r => r.GetGuid(0), cancellationToken, AfterParameters(after));
        return tail.Count == 0 ? null : tail[0];
    }

    private static NpgsqlParameter[] AfterParameters(Guid? after, params NpgsqlParameter[] extra) =>
        after is { } id ? [.. extra, new NpgsqlParameter("after", id)] : extra;

    private static string IdRange(string column, Guid? after) =>
        after is null ? $"{column} <= @through" : $"{column} > @after AND {column} <= @through";

    private static NpgsqlParameter[] IdRangeParameters(Guid? after, Guid through) =>
        AfterParameters(after, new NpgsqlParameter("through", through));
}
