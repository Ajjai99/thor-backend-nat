using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Thor.DataLayer.Data;
using Thor.DataLayer.Models.Tenants;

namespace Thor.Workflows.Pai.Privilege;

/// <summary>
/// Brings the tenant's <c>privilege_definition</c> rows in line with the config shipped in the image,
/// keyed on <c>(connector_type, name)</c>. Runs at the start of every PAI run because a config change
/// produces no ingest event and has nothing else to ride in on.
///
/// <para>Rows are updated in place, never replaced — the id is a foreign key from every
/// <c>pai_result</c> row. Config is the source of truth for what is active: a shipped definition is
/// reactivated, and one no longer shipped is deactivated (its routes are removed at finalize).</para>
/// </summary>
internal static class PrivilegeDefinitionSeeder
{
    private const string DeclarativePatternType = "declarative";

    public static async Task SeedAsync(
        TenantDbContext db, IReadOnlyList<ConfiguredPrivilegeDefinition> configured, ILogger logger, CancellationToken cancellationToken)
    {
        var existing = await db.PrivilegeDefinitions.ToDictionaryAsync(d => (d.ConnectorType, d.Name), cancellationToken);
        var now = DateTimeOffset.UtcNow;
        var inserted = 0;
        var updated = 0;

        foreach (var definition in configured)
        {
            if (!existing.Remove((definition.ConnectorType, definition.Name), out var row))
            {
                db.PrivilegeDefinitions.Add(new PrivilegeDefinition
                {
                    Id = Guid.NewGuid(),
                    ConnectorType = definition.ConnectorType,
                    Name = definition.Name,
                    Description = definition.Description,
                    PatternType = DeclarativePatternType,
                    PathPattern = definition.PathPatternJson,
                    DefinitionHash = definition.DefinitionHash,
                    Severity = definition.Severity,
                    IsActive = true,
                    CreatedAt = now,
                    UpdatedAt = now,
                });
                inserted++;
                continue;
            }

            if (row.Description == definition.Description && row.PathPattern == definition.PathPatternJson
                && row.DefinitionHash == definition.DefinitionHash && row.Severity == definition.Severity && row.IsActive)
            {
                continue;
            }

            row.Description = definition.Description;
            row.PatternType = DeclarativePatternType;
            row.PathPattern = definition.PathPatternJson;
            row.DefinitionHash = definition.DefinitionHash;
            row.Severity = definition.Severity;
            row.IsActive = true;
            row.UpdatedAt = now;
            updated++;
        }

        // What is left was not shipped.
        var deactivated = 0;
        foreach (var row in existing.Values.Where(r => r.IsActive))
        {
            row.IsActive = false;
            row.UpdatedAt = now;
            deactivated++;
        }

        if (inserted + updated + deactivated > 0)
        {
            await db.SaveChangesAsync(cancellationToken);
            logger.LogInformation(
                "Privilege definitions seeded: {Inserted} inserted, {Updated} updated, {Deactivated} deactivated.",
                inserted, updated, deactivated);
        }
    }
}
