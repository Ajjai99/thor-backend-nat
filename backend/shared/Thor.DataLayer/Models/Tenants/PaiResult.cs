using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Thor.DataLayer.Models.Tenants;

/// <summary>
/// A privileged-access-evaluation result: one <b>route</b> by which an account reaches a target (a
/// group or asset, identified polymorphically by <see cref="TargetType"/>/<see cref="TargetId"/>)
/// matching a <see cref="PrivilegeDefinition"/>. Two routes to the same target are two rows; a
/// consumer that wants the account-level answer rolls up itself.
///
/// <para>Unique on (account, target, definition, <see cref="AccessPathHash"/>) so a retried or
/// re-driven PAI run collapses onto the same row. The hash carries the uniqueness because the path
/// is an unbounded JSON document.</para>
/// </summary>
[Table("pai_result")]
[Index(nameof(AccountId), nameof(TargetType), nameof(TargetId), nameof(PrivilegeDefinitionId), nameof(AccessPathHash), IsUnique = true)]
[Index(nameof(PrivilegeDefinitionId), nameof(TargetId))]
[Index(nameof(AccountId), nameof(IsActive))]
public sealed class PaiResult
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; }

    [Column("account_id")]
    [ForeignKey(nameof(Account))]
    public Guid AccountId { get; set; }

    [Column("target_id")]
    public Guid TargetId { get; set; }

    [Column("target_type")]
    public string TargetType { get; set; } = null!;

    /// <summary>The route, account first, as a JSON array of <c>{ node_id, node_type, edge_type }</c> hops.</summary>
    [Column("access_path")]
    public string AccessPath { get; set; } = null!;

    /// <summary>SHA-256 of <see cref="AccessPath"/>, so the route can take part in the unique index.</summary>
    [Column("access_path_hash")]
    public string AccessPathHash { get; set; } = null!;

    /// <summary>
    /// Every non-account node on the route, target included. GIN-indexed (see
    /// <c>TenantDbContext</c>) so an incremental PAI run can find the routes through a group that
    /// changed or vanished — routes the graph can no longer walk once the group is gone.
    /// </summary>
    [Column("via_node_ids")]
    public Guid[] ViaNodeIds { get; set; } = [];

    [Column("privilege_definition_id")]
    [ForeignKey(nameof(PrivilegeDefinition))]
    public Guid PrivilegeDefinitionId { get; set; }

    [Column("is_privileged")]
    public bool IsPrivileged { get; set; }

    [Column("is_active")]
    public bool IsActive { get; set; }

    /// <summary>The PAI run that last proved this route.</summary>
    [Column("run_id")]
    public Guid RunId { get; set; }

    /// <summary>Database time of the last run that proved this route — what a later run's prune compares against.</summary>
    [Column("computed_at")]
    public DateTimeOffset ComputedAt { get; set; }

    public Account Account { get; set; } = null!;

    public PrivilegeDefinition PrivilegeDefinition { get; set; } = null!;
}
