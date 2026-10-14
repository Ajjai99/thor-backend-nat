using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Thor.DataLayer.Models.Tenants;

/// <summary>
/// A pattern (an access path shape) used to evaluate whether an account holds a privileged
/// grant, producing <see cref="PaiResult"/> rows. Per connector and seeded from config shipped with
/// the PAI workflow, so unique on (<see cref="ConnectorType"/>, <see cref="Name"/>).
/// </summary>
[Table("privilege_definition")]
[Index(nameof(ConnectorType), nameof(Name), IsUnique = true)]
public sealed class PrivilegeDefinition
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; }

    /// <summary>Which connector's data this definition applies to; its targets must carry the same connector type.</summary>
    [Column("connector_type")]
    public short ConnectorType { get; set; }

    [Column("name")]
    public string Name { get; set; } = null!;

    [Column("description")]
    public string Description { get; set; } = null!;

    [Column("pattern_type")]
    public string PatternType { get; set; } = null!;

    /// <summary>The rule, as a JSON document: <c>{ edgeType, targetType, match: { field, operator, value(s) } }</c>.</summary>
    [Column("path_pattern")]
    public string PathPattern { get; set; } = null!;

    /// <summary>SHA-256 of <see cref="PathPattern"/> as currently configured.</summary>
    [Column("definition_hash")]
    public string? DefinitionHash { get; set; }

    /// <summary>
    /// <see cref="DefinitionHash"/> as of the last PAI run that completed with it. While the two
    /// differ — a new or edited definition — PAI recomputes the definition in full rather than from
    /// the change set, because a config edit produces no change event.
    /// </summary>
    [Column("computed_hash")]
    public string? ComputedHash { get; set; }

    [Column("severity")]
    public string Severity { get; set; } = null!;

    [Column("is_active")]
    public bool IsActive { get; set; }

    [Column("created_at")]
    public DateTimeOffset CreatedAt { get; set; }

    [Column("updated_at")]
    public DateTimeOffset UpdatedAt { get; set; }

    private readonly List<PaiResult> _paeResults = new();
    public IEnumerable<PaiResult> PaeResults => _paeResults;
}
