using System.Text.RegularExpressions;

namespace Thor.Graph;

/// <summary>How a <see cref="PrivilegePattern"/> compares a vertex property to its value(s).</summary>
public enum PrivilegeMatchOperator
{
    Equals,
    Contains,
    StartsWith,

    /// <summary>Matches any of <see cref="PrivilegePattern.Values"/> — one definition covers a curated list.</summary>
    In,
}

/// <summary>
/// Which vertices count as a privileged target, and which edge leads to them. The query descriptor
/// only — the JSON that produces one and its field allow-list belong to the PAI workflow module.
///
/// <para><b>Matching is case-sensitive.</b> Gremlin has no case-insensitive text predicate portable to
/// Neptune, so values must be spelled as the connector stores them.</para>
/// </summary>
/// <param name="EdgeType">
/// Relationship from account up to target. <c>MEMBER_OF</c> is followed through nested groups; every
/// other type is a single hop.
/// </param>
/// <param name="TargetLabel">Vertex label of the privileged target — <c>grp</c> or <c>asset</c>.</param>
/// <param name="Field">Vertex property to test, in the graph's camelCase spelling (<c>displayName</c>, <c>assetType</c>, …).</param>
/// <param name="Values"><see cref="PrivilegeMatchOperator.In"/> matches any; every other operator requires exactly one.</param>
/// <param name="ConnectorType">
/// When set, a target must also carry this <c>connectorType</c>. Without it a group name such as
/// "Administrators" matches one connector's definition against another connector's groups.
/// </param>
public sealed record PrivilegePattern(
    string EdgeType,
    string TargetLabel,
    string Field,
    PrivilegeMatchOperator Operator,
    IReadOnlyList<string> Values,
    int? ConnectorType = null)
{
    public const string MemberOf = "MEMBER_OF";

    /// <summary>
    /// Property keys come from privilege config rather than code, so their shape is checked when the
    /// pattern is built, not discovered as an empty result.
    /// </summary>
    private static readonly Regex SafeFieldName = new("^[A-Za-z][A-Za-z0-9]*$", RegexOptions.Compiled);

    public string EdgeType { get; } = Require(EdgeType, nameof(EdgeType));

    public string TargetLabel { get; } = Require(TargetLabel, nameof(TargetLabel));

    public string Field { get; } = SafeFieldName.IsMatch(Field)
        ? Field
        : throw new ArgumentException($"'{Field}' is not a valid graph property name (letters and digits only).", nameof(Field));

    public IReadOnlyList<string> Values { get; } =
        Values is { Count: > 0 } && (Operator == PrivilegeMatchOperator.In || Values.Count == 1)
            ? Values
            : throw new ArgumentException(
                $"{Operator} requires {(Operator == PrivilegeMatchOperator.In ? "at least one value" : "exactly one value")}.",
                nameof(Values));

    /// <summary>Whether routes reach the target through nested groups rather than a single hop.</summary>
    public bool IsTransitive => EdgeType == MemberOf;

    private static string Require(string value, string name) => string.IsNullOrWhiteSpace(value)
        ? throw new ArgumentException($"{name} is required.", name)
        : value;
}
