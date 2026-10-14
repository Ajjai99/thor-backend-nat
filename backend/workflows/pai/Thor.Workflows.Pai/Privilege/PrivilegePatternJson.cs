using System.Text.Json;
using System.Text.Json.Serialization;
using Thor.Graph;
using Thor.Workflows.Pai.Constants;

namespace Thor.Workflows.Pai.Privilege;

/// <summary>
/// Binds the JSON form of a privilege rule to the graph query it describes, and rejects anything it
/// cannot mean. A misspelt field or operator would otherwise produce a definition that silently
/// matches nothing — indistinguishable from "this tenant has no privileged accounts".
///
/// <para>Used both ways: the seeder validates config through it before writing a row, and a run
/// parses <c>privilege_definition.path_pattern</c> back through it.</para>
/// </summary>
internal static class PrivilegePatternJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// String vertex properties a pattern may test, per target label — what graph-load's
    /// <c>GraphVertexProjection</c> writes. Kept by hand because workflow modules do not reference each
    /// other. Strings only: pattern values are strings, and a typed graph value never equals one.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, IReadOnlySet<string>> MatchableFields =
        new Dictionary<string, IReadOnlySet<string>>
        {
            [GraphLabels.Group] = new HashSet<string>(StringComparer.Ordinal)
            {
                "nativeId", "groupClass", "displayName", "email", "domainName",
            },
            [GraphLabels.Asset] = new HashSet<string>(StringComparer.Ordinal)
            {
                "nativeId", "assetType", "displayName", "fullPath", "filerName",
            },
        };

    private static readonly IReadOnlyDictionary<string, PrivilegeMatchOperator> Operators =
        new Dictionary<string, PrivilegeMatchOperator>(StringComparer.Ordinal)
        {
            ["equals"] = PrivilegeMatchOperator.Equals,
            ["contains"] = PrivilegeMatchOperator.Contains,
            ["startsWith"] = PrivilegeMatchOperator.StartsWith,
            ["in"] = PrivilegeMatchOperator.In,
        };

    public static PrivilegePattern Parse(string json, string origin, short connectorType)
    {
        PathPatternDto? dto;
        try
        {
            dto = JsonSerializer.Deserialize<PathPatternDto>(json, Options);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"{origin}: pathPattern is not valid JSON — {ex.Message}", ex);
        }

        return ToPattern(dto ?? throw new InvalidOperationException($"{origin}: pathPattern was null."), origin, connectorType);
    }

    public static PrivilegePattern ToPattern(PathPatternDto dto, string origin, short connectorType)
    {
        if (string.IsNullOrWhiteSpace(dto.EdgeType))
        {
            throw new InvalidOperationException($"{origin}: pathPattern.edgeType is required.");
        }

        if (!MatchableFields.TryGetValue(dto.TargetType ?? string.Empty, out var fields))
        {
            throw new InvalidOperationException(
                $"{origin}: unknown pathPattern.targetType '{dto.TargetType}'. Known: {string.Join(", ", MatchableFields.Keys)}.");
        }

        var match = dto.Match ?? throw new InvalidOperationException($"{origin}: pathPattern.match is required.");

        if (!Operators.TryGetValue(match.Operator ?? string.Empty, out var op))
        {
            throw new InvalidOperationException(
                $"{origin}: unknown match.operator '{match.Operator}'. Known: {string.Join(", ", Operators.Keys)}.");
        }

        if (match.Field is null || !fields.Contains(match.Field))
        {
            throw new InvalidOperationException(
                $"{origin}: '{match.Field}' is not a matchable property of a '{dto.TargetType}' vertex. Known: {string.Join(", ", fields.Order())}.");
        }

        // `value` and `values` are the same thing at one and many; accepting only the shape that fits
        // the operator keeps a list from being written as one string that silently matches nothing.
        var values = match.Values ?? (match.Value is null ? [] : [match.Value]);

        try
        {
            return new PrivilegePattern(dto.EdgeType, dto.TargetType!, match.Field, op, values, connectorType);
        }
        catch (ArgumentException ex)
        {
            throw new InvalidOperationException($"{origin}: {ex.Message}", ex);
        }
    }

    /// <summary>Re-serialized from the parsed shape, so two files differing only in whitespace or key order store — and hash — identically.</summary>
    public static string Canonicalize(PathPatternDto dto) => JsonSerializer.Serialize(dto, Options);

    public sealed record PathPatternDto(string? EdgeType, string? TargetType, MatchDto? Match);

    public sealed record MatchDto(
        string? Field,
        [property: JsonPropertyName("operator")] string? Operator,
        string? Value = null,
        IReadOnlyList<string>? Values = null);
}
