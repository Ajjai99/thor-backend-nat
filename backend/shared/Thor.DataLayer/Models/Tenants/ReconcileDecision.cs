using Microsoft.EntityFrameworkCore;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Thor.DataLayer.Models.Tenants;

/// <summary>Values of <see cref="ReconcileDecision.Outcome"/>.</summary>
public static class ReconcileOutcome
{
    /// <summary>Within the threshold: the absent rows are soft-deleted.</summary>
    public const string Deleted = "deleted";

    /// <summary>Over the threshold: the absent rows are flagged for an administrator and left alone.</summary>
    public const string Proposed = "proposed";

    /// <summary>Nothing was absent.</summary>
    public const string NothingAbsent = "nothing_absent";

    /// <summary>The source was not swept; <see cref="ReconcileDecision.SkipReason"/> says why.</summary>
    public const string Skipped = "skipped";
}

/// <summary>
/// What one reconcile run decided for one (source, entity type) of a scan, and the numbers it decided
/// on — the record an administrator reads to see what was deleted, what is waiting on them, and which
/// sources were not swept. Written once per run by the reconcile workflow's start step.
///
/// <para>No foreign keys, like <see cref="Account.DeletionProposedScanId"/>: an audit record must never
/// stop a scan or source being removed.</para>
/// </summary>
[Table("reconcile_decision")]
[Index(nameof(ScanId))]
[Index(nameof(RunId))]
public sealed class ReconcileDecision
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; }

    [Column("run_id")]
    public Guid RunId { get; set; }

    [Column("scan_id")]
    public Guid ScanId { get; set; }

    [Column("source_id")]
    public Guid SourceId { get; set; }

    /// <summary><c>account</c>, <c>grp</c>, <c>asset</c> or <c>entitlement</c>.</summary>
    [Column("entity_type")]
    public string EntityType { get; set; } = null!;

    /// <summary>One of <see cref="ReconcileOutcome"/>.</summary>
    [Column("outcome")]
    public string Outcome { get; set; } = null!;

    /// <summary>Why a <see cref="ReconcileOutcome.Skipped"/> source was not swept; null otherwise.</summary>
    [Column("skip_reason")]
    public string? SkipReason { get; set; }

    /// <summary>The source's rows of this type not already deleted — the threshold's denominator. Null when skipped.</summary>
    [Column("live_count")]
    public long? LiveCount { get; set; }

    /// <summary>Live rows the scan did not deliver. Null when skipped.</summary>
    [Column("absent_count")]
    public long? AbsentCount { get; set; }

    /// <summary>The source's threshold when the decision was made.</summary>
    [Column("threshold_percent")]
    public short ThresholdPercent { get; set; }

    /// <summary>Earlier proposals cleared because the scan saw those rows again.</summary>
    [Column("proposals_withdrawn")]
    public int ProposalsWithdrawn { get; set; }

    [Column("decided_at")]
    public DateTimeOffset DecidedAt { get; set; }
}
