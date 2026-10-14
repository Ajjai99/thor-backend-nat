using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Thor.DataLayer.Models.Tenants;

/// <summary>
/// A detected insert/update/delete on an entity (identified polymorphically by
/// <see cref="EntityType"/>/<see cref="EntityId"/>) produced by the CDC diff for a
/// <see cref="ScanManifest"/> (see ADR §12). The (ScanId, EntityType) index serves PAI, which reads
/// a set of scans' changes by entity type.
/// </summary>
[Table("ingest_change_event")]
[Index(nameof(ScanId), nameof(EntityType))]
public sealed class IngestChangeEvent
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; }

    /// <summary>
    /// The manifest whose promotion produced this event, or <c>null</c> for one that belongs to the
    /// scan but to no manifest — the reconcile workflow's deletions, detected by an entity's absence
    /// from every manifest. Manifest-scoped readers compare against a concrete id and so skip these;
    /// scan-scoped ones (the graph load, PAI) see them.
    /// </summary>
    [Column("scan_manifest_id")]
    [ForeignKey(nameof(ScanManifest))]
    public Guid? ScanManifestId { get; set; }

    [Column("scan_id")]
    [ForeignKey(nameof(Scan))]
    public Guid ScanId { get; set; }

    [Column("entity_type")]
    public string EntityType { get; set; } = null!;

    [Column("entity_id")]
    public Guid EntityId { get; set; }

    [Column("change_type")]
    public string ChangeType { get; set; } = null!;

    [Column("occurred_at")]
    public DateTimeOffset OccurredAt { get; set; }

    public ScanManifest? ScanManifest { get; set; }

    public Scan Scan { get; set; } = null!;
}
