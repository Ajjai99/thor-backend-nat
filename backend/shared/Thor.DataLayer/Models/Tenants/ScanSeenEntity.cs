using Microsoft.EntityFrameworkCore;
using System.ComponentModel.DataAnnotations.Schema;

namespace Thor.DataLayer.Models.Tenants;

/// <summary>
/// One row per entity a <see cref="Scan"/> delivered — the ledger that makes <em>absence</em>
/// detectable. Written by promotion from staging, read by the reconcile workflow as an anti-join, and
/// dropped once that scan has been reconciled.
///
/// <para>Nothing else records "seen". Promotion only writes rows whose content changed, so a canonical
/// row's <c>updated_at</c> means <em>last changed</em>, and the staging rows that would answer the
/// question are deleted batch by batch. Absence has to be recorded while the export is in hand.</para>
///
/// <para>Not a <c>last_seen_scan_id</c> column on the canonical tables: Postgres has no in-place
/// update, so stamping 1M accounts would rewrite 1M whole tuples. The ledger is insert-only and small
/// per row. No foreign keys either, so a full scan's inserts pay no trigger checks.</para>
///
/// <para>Keep it LOGGED. <c>UNLOGGED</c> is truncated on crash recovery, and an empty ledger reads as
/// <em>every entity is absent</em>; the reconcile workflow refuses to sweep without one.</para>
/// </summary>
[Table("scan_seen_entity")]
[PrimaryKey(nameof(ScanId), nameof(SourceId), nameof(EntityType), nameof(NativeId))]
public sealed class ScanSeenEntity
{
    [Column("scan_id")]
    public Guid ScanId { get; set; }

    [Column("source_id")]
    public Guid SourceId { get; set; }

    /// <summary><c>account</c>, <c>grp</c>, <c>asset</c> or <c>entitlement</c> — <see cref="IngestChangeEvent.EntityType"/>'s vocabulary.</summary>
    [Column("entity_type")]
    public string EntityType { get; set; } = null!;

    /// <summary>The connector's own identifier — the canonical table's <c>(source_id, native_id)</c> key, which the sweep anti-joins on.</summary>
    [Column("native_id")]
    public string NativeId { get; set; } = null!;
}
