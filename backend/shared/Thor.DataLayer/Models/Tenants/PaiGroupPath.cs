using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Thor.DataLayer.Models.Tenants;

/// <summary>
/// Per-run staging for PAI: one simple path down the group layer from a target, target first; depth
/// 0 is the target alone. PAI's fan-out chunks read windows of these rows by <see cref="Id"/> and
/// join each path's last node to its direct account members. Deleted when the run finalizes; a
/// failed run keeps its rows so a retry resumes.
/// </summary>
[Table("pai_group_path")]
[Index(nameof(RunId), nameof(Id))]
[Index(nameof(RunId), nameof(Depth))]
[Index(nameof(RunId), nameof(DefinitionId), nameof(TargetId), nameof(NodeIds), IsUnique = true)]
public sealed class PaiGroupPath
{
    [Key]
    [Column("id")]
    public long Id { get; set; }

    [Column("run_id")]
    public Guid RunId { get; set; }

    [Column("definition_id")]
    public Guid DefinitionId { get; set; }

    [Column("target_id")]
    public Guid TargetId { get; set; }

    [Column("depth")]
    public int Depth { get; set; }

    [Column("last_node_id")]
    public Guid LastNodeId { get; set; }

    /// <summary>The path, target first.</summary>
    [Column("node_ids")]
    public Guid[] NodeIds { get; set; } = [];

    /// <summary>Set when the fan-out chunk that owned this row failed. Its target is then never pruned.</summary>
    [Column("failed")]
    public bool Failed { get; set; }
}
