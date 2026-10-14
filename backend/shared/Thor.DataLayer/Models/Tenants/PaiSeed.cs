using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Thor.DataLayer.Models.Tenants;

/// <summary>
/// Per-run staging for PAI: a target one run recomputes (<c>compute</c>), or whose routes it removes
/// because the target no longer matches its definition (<c>retire</c>). Deleted when the run finalizes.
/// </summary>
[Table("pai_seed")]
[PrimaryKey(nameof(RunId), nameof(DefinitionId), nameof(TargetId))]
public sealed class PaiSeed
{
    [Column("run_id")]
    public Guid RunId { get; set; }

    [Column("definition_id")]
    public Guid DefinitionId { get; set; }

    [Column("target_id")]
    public Guid TargetId { get; set; }

    [Column("action")]
    public string Action { get; set; } = null!;
}
