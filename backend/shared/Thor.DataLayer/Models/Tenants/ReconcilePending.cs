using Microsoft.EntityFrameworkCore;
using System.ComponentModel.DataAnnotations.Schema;

namespace Thor.DataLayer.Models.Tenants;

/// <summary>
/// The entities one reconcile run decided to delete, staged by its start step and worked through by its
/// delete waves in <see cref="Seq"/> windows. Freezing the set at decision time means the threshold
/// decision and what is actually deleted cannot drift apart while the waves run. Dropped when the run
/// finishes.
/// </summary>
[Table("reconcile_pending")]
[PrimaryKey(nameof(RunId), nameof(Seq))]
public sealed class ReconcilePending
{
    [Column("run_id")]
    public Guid RunId { get; set; }

    /// <summary>1..N within the run, contiguous: a chunk is a window <c>(from, to]</c> over it.</summary>
    [Column("seq")]
    public long Seq { get; set; }

    /// <summary><c>account</c>, <c>grp</c>, <c>asset</c> or <c>entitlement</c> — also the canonical table's name.</summary>
    [Column("entity_type")]
    public string EntityType { get; set; } = null!;

    [Column("entity_id")]
    public Guid EntityId { get; set; }
}
