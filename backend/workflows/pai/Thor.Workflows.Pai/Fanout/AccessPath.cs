using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Thor.Graph;
using Thor.Workflows.Pai.Constants;

namespace Thor.Workflows.Pai.Fanout;

/// <summary>
/// Serialises a route into the <c>pai_result.access_path</c> document and the hash that stands in for
/// it in the unique index.
/// </summary>
internal static class AccessPath
{
    /// <summary>Snake_case pinned here rather than inherited: report consumers read this document, so its keys are a contract.</summary>
    private static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    private sealed record Hop(Guid NodeId, string NodeType, string? EdgeType);

    /// <summary>
    /// The route account first, then the group path from its bottom up to the target. Each hop names
    /// the edge followed out of it; the target's is null. Byte-identical for the same route on every
    /// run — every value is an id or a fixed label — which is what makes the hash an idempotency key.
    /// </summary>
    /// <param name="groupPath">The staged path, target first.</param>
    public static (string Json, string Hash) Serialize(Guid accountId, IReadOnlyList<Guid> groupPath, PrivilegePattern pattern)
    {
        var hops = new List<Hop>(groupPath.Count + 1) { new(accountId, GraphLabels.Account, pattern.EdgeType) };
        for (var i = groupPath.Count - 1; i >= 0; i--)
        {
            hops.Add(i == 0
                ? new Hop(groupPath[i], pattern.TargetLabel, null)
                : new Hop(groupPath[i], GraphLabels.Group, pattern.EdgeType));
        }

        var json = JsonSerializer.Serialize(hops, Options);
        return (json, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(json))));
    }
}
