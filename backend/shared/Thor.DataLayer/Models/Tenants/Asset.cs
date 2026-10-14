using Microsoft.EntityFrameworkCore;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Thor.DataLayer.Models.Tenants;

/// <summary>
/// A canonical asset (file, folder, share, etc.) record, reconciled from staged
/// ingestion rows (see ADR §12 Ingestion &amp; CDC) against this table.
/// </summary>
[Table("asset")]
[Index(nameof(SourceId), nameof(NativeId), IsUnique = true)]
public sealed class Asset
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; }

    [Column("source_id")]
    [ForeignKey(nameof(Source))]
    public Guid SourceId { get; set; }

    [Column("connector_type")]
    public short ConnectorType { get; set; }

    [Column("native_id")]
    public string NativeId { get; set; } = null!;

    [Column("asset_type")]
    public string AssetType { get; set; } = null!;

    [Column("display_name")]
    public string DisplayName { get; set; } = null!;

    [Column("full_path")]
    public string FullPath { get; set; } = null!;

    [Column("filer_name")]
    public string FilerName { get; set; } = null!;

    [Column("file_size")]
    public long FileSize { get; set; }

    [Column("file_count")]
    public long FileCount { get; set; }

    [Column("broken_acl")]
    public bool BrokenAcl { get; set; }

    [Column("is_protected")]
    public bool IsProtected { get; set; }

    [Column("parent_asset_id")]
    [ForeignKey(nameof(ParentAsset))]
    public Guid? ParentAssetId { get; set; }

    /// <summary>
    /// Soft-delete marker. No connector sends one for assets, so only the reconcile workflow sets it —
    /// for an asset absent from a successful full export. Promotion resets it to <c>false</c>, which
    /// revives an asset the source sends again.
    /// </summary>
    [Column("is_deleted")]
    public bool IsDeleted { get; set; }

    /// <summary>Set by the reconcile workflow instead of deleting, when the absence exceeded the source's threshold. See <see cref="Account.DeletionProposedAt"/>.</summary>
    [Column("deletion_proposed_at")]
    public DateTimeOffset? DeletionProposedAt { get; set; }

    /// <summary>The scan whose absence raised <see cref="DeletionProposedAt"/>. Not a foreign key.</summary>
    [Column("deletion_proposed_scan_id")]
    public Guid? DeletionProposedScanId { get; set; }

    [Column("raw_attributes")]
    public string RawAttributes { get; set; } = null!;

    [Column("content_hash")]
    public string ContentHash { get; set; } = null!;

    [Column("hash_version")]
    public short HashVersion { get; set; }

    [Column("created_at")]
    public DateTimeOffset CreatedAt { get; set; }

    [Column("updated_at")]
    public DateTimeOffset UpdatedAt { get; set; }

    public Source Source { get; set; } = null!;

    public Asset? ParentAsset { get; set; } 
}
