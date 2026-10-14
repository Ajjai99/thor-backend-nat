using Microsoft.EntityFrameworkCore;
using Thor.DataLayer.Data;
using Thor.Graph;

namespace Thor.Workflows.Pai.Privilege;

/// <summary>One definition as a given run evaluates it: its parsed pattern, and whether the run recomputes it in full.</summary>
internal sealed record RunDefinition(Guid Id, string Name, PrivilegePattern Pattern, bool IsFull)
{
    /// <summary>The definitions a run fixed at start (<c>pai_run_definition</c>), keyed by id.</summary>
    public static async Task<IReadOnlyDictionary<Guid, RunDefinition>> LoadAsync(
        TenantDbContext db, Guid runId, CancellationToken cancellationToken)
    {
        var rows = await (
            from runDefinition in db.PaiRunDefinitions.AsNoTracking()
            join definition in db.PrivilegeDefinitions.AsNoTracking() on runDefinition.DefinitionId equals definition.Id
            where runDefinition.RunId == runId
            select new { definition.Id, definition.Name, definition.PathPattern, definition.ConnectorType, runDefinition.IsFull })
            .ToListAsync(cancellationToken);

        return rows.ToDictionary(
            row => row.Id,
            row => new RunDefinition(row.Id, row.Name, PrivilegePatternJson.Parse(row.PathPattern, row.Name, row.ConnectorType), row.IsFull));
    }
}
