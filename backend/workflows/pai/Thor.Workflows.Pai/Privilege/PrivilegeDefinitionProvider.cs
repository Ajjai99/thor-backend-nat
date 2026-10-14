using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Thor.Workflows.Pai.Privilege;

/// <summary>One privilege definition as configured on disk, already validated.</summary>
internal sealed record ConfiguredPrivilegeDefinition(
    short ConnectorType, string Name, string Description, string Severity, string PathPatternJson, string DefinitionHash);

/// <summary>
/// Loads every privilege-definition JSON file under <c>Privilege/Config</c> next to the running
/// assembly, validating each, so a malformed rule fails the run at start rather than matching nothing.
/// </summary>
internal sealed class PrivilegeDefinitionProvider
{
    private readonly List<ConfiguredPrivilegeDefinition> _definitions = [];

    public PrivilegeDefinitionProvider(string? configDirectory = null)
    {
        var dir = configDirectory ?? Path.Combine(AppContext.BaseDirectory, "Privilege", "Config");

        foreach (var file in Directory.EnumerateFiles(dir, "*.json").Order())
        {
            var fileName = Path.GetFileName(file);
            var dto = JsonSerializer.Deserialize<ConnectorFileDto>(File.ReadAllText(file), PrivilegePatternJson.Options)
                ?? throw new InvalidOperationException($"Privilege definition file '{fileName}' did not deserialize.");

            foreach (var definition in dto.Definitions)
            {
                if (string.IsNullOrWhiteSpace(definition.Name))
                {
                    throw new InvalidOperationException($"{fileName}: every definition needs a name.");
                }

                var origin = $"{fileName} → '{definition.Name}'";
                var pathPattern = definition.PathPattern ?? throw new InvalidOperationException($"{origin}: pathPattern is required.");
                var connectorType = (short)dto.ConnectorType;

                // Validates; the parsed pattern itself is rebuilt from the stored row at run time.
                PrivilegePatternJson.ToPattern(pathPattern, origin, connectorType);
                var json = PrivilegePatternJson.Canonicalize(pathPattern);

                _definitions.Add(new ConfiguredPrivilegeDefinition(
                    connectorType, definition.Name, definition.Description ?? string.Empty, definition.Severity ?? string.Empty,
                    json, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(json)))));
            }
        }

        // (connector_type, name) is the seeder's key, so a duplicate would silently overwrite the first.
        var duplicate = _definitions.GroupBy(d => (d.ConnectorType, d.Name)).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
        {
            throw new InvalidOperationException(
                $"Duplicate privilege definition '{duplicate.Key.Name}' for connector type {duplicate.Key.ConnectorType}.");
        }
    }

    public IReadOnlyList<ConfiguredPrivilegeDefinition> GetAll() => _definitions;

    private sealed record ConnectorFileDto(int ConnectorType, IReadOnlyList<DefinitionDto> Definitions);

    private sealed record DefinitionDto(string? Name, string? Description, string? Severity, PrivilegePatternJson.PathPatternDto? PathPattern);
}
