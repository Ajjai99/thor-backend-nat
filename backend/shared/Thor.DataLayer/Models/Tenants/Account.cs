using Microsoft.EntityFrameworkCore;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Thor.DataLayer.Models.Tenants;

/// <summary>
/// A canonical account/identity record, reconciled from staged ingestion rows (see
/// ADR §12 Ingestion &amp; CDC) against this table.
/// </summary>
[Table("account")]
[Index(nameof(SourceId), nameof(NativeId), IsUnique = true)]
public sealed class Account
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

    [Column("account_kind")]
    public string AccountKind { get; set; } = null!;

    [Column("is_human")]
    public bool IsHuman { get; set; }

    [Column("display_name")]
    public string DisplayName { get; set; } = null!;

    [Column("sam_account_name")]
    public string SamAccountName { get; set; } = null!;

    [Column("upn")]
    public string Upn { get; set; } = null!;

    [Column("email")]
    public string Email { get; set; } = null!;

    [Column("domain_name")]
    public string DomainName { get; set; } = null!;

    [Column("filer_name")]
    public string FilerName { get; set; } = null!;

    [Column("native_account_id")]
    public string NativeAccountId { get; set; } = null!;

    [Column("is_deleted")]
    public bool IsDeleted { get; set; }

    [Column("is_disabled")]
    public bool IsDisabled { get; set; }

    /// <summary>
    /// Set by the reconcile workflow when this account was absent from a scan but its source lost
    /// more than <see cref="Source.AutoDeleteThresholdPercent"/> of its accounts — too many to delete
    /// unattended. Nothing else about the row changes; an administrator decides. Stamped when first
    /// proposed and left alone while the account stays absent (each scan's view is in
    /// <see cref="ReconcileDecision"/>); cleared when the account reappears in a scan.
    /// </summary>
    [Column("deletion_proposed_at")]
    public DateTimeOffset? DeletionProposedAt { get; set; }

    /// <summary>The scan that first proposed the deletion. Not a foreign key: a diagnostic breadcrumb, never a reason a scan cannot be removed.</summary>
    [Column("deletion_proposed_scan_id")]
    public Guid? DeletionProposedScanId { get; set; }

    [Column("account_type_id")]
    [ForeignKey(nameof(AccountType))]
    public Guid AccountTypeId { get; set; }

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

    public AccountType AccountType { get; set; } = null!;

    private readonly List<PaiResult> _paiResults = new();
    public IEnumerable<PaiResult> PaiResults => _paiResults;
}
