using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Thor.DataLayer.Models.Tenants;

/// <summary>
/// Per-run staging for PAI: how one run evaluates one definition, fixed when the run starts so its
/// finalize step prunes by the same decision even if the definition is re-seeded in between.
/// Deleted when the run finalizes.
/// </summary>
[Table("pai_run_definition")]
[PrimaryKey(nameof(RunId), nameof(DefinitionId))]
public sealed class PaiRunDefinition
{
    [Column("run_id")]
    public Guid RunId { get; set; }

    [Column("definition_id")]
    public Guid DefinitionId { get; set; }

    /// <summary>True when every matching target is recomputed; false when only the change set's are.</summary>
    [Column("is_full")]
    public bool IsFull { get; set; }

    /// <summary>The definition's hash at run start — what its computed hash advances to if the run completes.</summary>
    [Column("definition_hash")]
    public string DefinitionHash { get; set; } = null!;

    /// <summary>Database time the run started. A route this run did not re-prove has an older computed_at.</summary>
    [Column("run_started_at")]
    public DateTimeOffset RunStartedAt { get; set; }
}
