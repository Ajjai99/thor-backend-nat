namespace Thor.Workflows.Reconcile;

/// <summary>Terminal statuses of a reconcile run's workflow row beyond the generic <c>completed</c> and <c>failed</c>.</summary>
public static class ReconcileStatuses
{
    /// <summary>The run finished, and at least one (source, entity type) was over its threshold: deletions await an administrator.</summary>
    public const string CompletedWithProposals = "completed_with_proposals";
}
