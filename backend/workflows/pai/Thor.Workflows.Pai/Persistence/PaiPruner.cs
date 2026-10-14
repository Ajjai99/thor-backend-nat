using Npgsql;
using Thor.DataLayer.Data;

namespace Thor.Workflows.Pai.Persistence;

internal sealed record PruneSummary(int Stale, int Retired, int DeletedAccounts, int InactiveDefinitions);

/// <summary>
/// Removes the routes a run is answerable for but did not re-prove. A route counts as re-proven when
/// its <c>computed_at</c> is not older than the run's start — database time on both sides.
///
/// <para>Comparing times rather than run ids is what makes two overlapping runs safe: a later run's
/// fresh rows are never older than an earlier run's start, so neither run deletes the other's work.
/// The cost of an overlap is a stale route surviving until the next run touches its target — the
/// safe direction.</para>
///
/// <para>What a run is answerable for: every target of a full definition, and the computed seeds of an
/// incremental one — except any target with a path in a failed fan-out chunk, whose routes were only
/// partly rewritten. Retired seeds lose all their routes. Routes of deleted accounts and of
/// deactivated definitions go regardless.</para>
/// </summary>
internal static class PaiPruner
{
    private const string NotInFailedChunk = """
        NOT EXISTS (
            SELECT 1 FROM tenant.pai_group_path p
            WHERE p.run_id = @run AND p.failed AND p.definition_id = r.privilege_definition_id AND p.target_id = r.target_id)
        """;

    public static async Task<PruneSummary> PruneAsync(TenantDbContext db, Guid runId, CancellationToken cancellationToken)
    {
        var stale = await PaiSql.ExecAsync(db, $"""
            DELETE FROM tenant.pai_result r
            USING tenant.pai_run_definition rd
            WHERE rd.run_id = @run AND rd.is_full
              AND r.privilege_definition_id = rd.definition_id
              AND r.computed_at < rd.run_started_at
              AND {NotInFailedChunk}
            """, [Run(runId)], cancellationToken);

        stale += await PaiSql.ExecAsync(db, $"""
            DELETE FROM tenant.pai_result r
            USING tenant.pai_seed s, tenant.pai_run_definition rd
            WHERE s.run_id = @run AND s.action = 'compute'
              AND rd.run_id = @run AND rd.definition_id = s.definition_id AND NOT rd.is_full
              AND r.privilege_definition_id = s.definition_id AND r.target_id = s.target_id
              AND r.computed_at < rd.run_started_at
              AND {NotInFailedChunk}
            """, [Run(runId)], cancellationToken);

        var retired = await PaiSql.ExecAsync(db, """
            DELETE FROM tenant.pai_result r
            USING tenant.pai_seed s
            WHERE s.run_id = @run AND s.action = 'retire'
              AND r.privilege_definition_id = s.definition_id AND r.target_id = s.target_id
            """, [Run(runId)], cancellationToken);

        var deletedAccounts = await PaiSql.ExecAsync(db, """
            DELETE FROM tenant.pai_result r
            USING tenant.account a
            WHERE r.account_id = a.id AND a.is_deleted
            """, [], cancellationToken);

        var inactiveDefinitions = await PaiSql.ExecAsync(db, """
            DELETE FROM tenant.pai_result r
            USING tenant.privilege_definition d
            WHERE r.privilege_definition_id = d.id AND NOT d.is_active
            """, [], cancellationToken);

        return new PruneSummary(stale, retired, deletedAccounts, inactiveDefinitions);
    }

    /// <summary>Records that each definition was fully evaluated at the hash the run started with, so the next run can be incremental for it.</summary>
    public static Task AdvanceComputedHashesAsync(TenantDbContext db, Guid runId, CancellationToken cancellationToken) =>
        PaiSql.ExecAsync(db, """
            UPDATE tenant.privilege_definition d
            SET computed_hash = rd.definition_hash
            FROM tenant.pai_run_definition rd
            WHERE rd.run_id = @run AND rd.definition_id = d.id
            """, [Run(runId)], cancellationToken);

    /// <summary>Drops the run's working state. <c>pai_run_scan</c> stays: it is the record of which scans the run covered.</summary>
    public static async Task DropStagingAsync(TenantDbContext db, Guid runId, CancellationToken cancellationToken)
    {
        foreach (var table in new[] { "pai_seed", "pai_group_path", "pai_run_definition" })
        {
            await PaiSql.ExecAsync(db, $"DELETE FROM tenant.{table} WHERE run_id = @run", [Run(runId)], cancellationToken);
        }
    }

    private static NpgsqlParameter Run(Guid runId) => new("run", runId);
}
