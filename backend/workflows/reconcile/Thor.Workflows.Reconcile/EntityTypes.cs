namespace Thor.Workflows.Reconcile;

/// <summary>
/// The four entity types the sweep reconciles. Each string is at once the <c>scan_seen_entity</c> value
/// promotion writes, the <c>ingest_change_event.entity_type</c> this module emits, and the canonical
/// table's name in the <c>tenant</c> schema, which the per-type SQL interpolates. A mismatch is silent —
/// the anti-join finds nothing absent, or everything — which is why they are named once, here.
/// </summary>
internal static class EntityTypes
{
    public const string Account = "account";

    public const string Group = "grp";

    public const string Asset = "asset";

    public const string Entitlement = "entitlement";

    public static readonly IReadOnlyList<string> All = [Account, Group, Asset, Entitlement];
}
