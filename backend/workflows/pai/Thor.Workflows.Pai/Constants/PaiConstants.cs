namespace Thor.Workflows.Pai.Constants;

/// <summary>
/// Vertex labels PAI works in. They are also <c>ingest_change_event.entity_type</c> values — the two
/// vocabularies are the same by construction, which is what lets a changed Postgres row be looked up
/// as a vertex.
/// </summary>
internal static class GraphLabels
{
    public const string Account = "account";
    public const string Group = "grp";
    public const string Asset = "asset";

    /// <summary>Not a label: the <c>entity_type</c> the edge gate stamps on edge change events.</summary>
    public const string Edge = "edge";
}

/// <summary>What a <c>pai_seed</c> row asks of the run.</summary>
internal static class SeedActions
{
    /// <summary>Recompute every route to the target.</summary>
    public const string Compute = "compute";

    /// <summary>The target no longer matches its definition: remove its routes.</summary>
    public const string Retire = "retire";
}

internal static class PaiStatuses
{
    /// <summary>Mirrors the literal <c>WorkflowLifecycle.MarkCompletedAsync</c> writes by default. Only this status covers a run's scans.</summary>
    public const string Completed = "completed";

    /// <summary>The run finished but at least one fan-out chunk was tolerated; its targets were not pruned.</summary>
    public const string CompletedWithErrors = "completed_with_errors";
}
