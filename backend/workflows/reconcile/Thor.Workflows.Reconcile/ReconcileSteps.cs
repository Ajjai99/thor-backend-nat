namespace Thor.Workflows.Reconcile;

/// <summary>
/// The <c>THOR_STEP</c> values this module answers to, named once so <c>Program.cs</c> and the
/// <c>steps</c> map in <c>infra/src/workflow_definitions.tf</c> cannot disagree silently.
/// </summary>
public static class ReconcileSteps
{
    /// <summary>Opens the run's row, checks the guards, decides per (source, entity type), proposes or stages the deletions and hands back wave zero.</summary>
    public const string StartRun = "start-run";

    /// <summary>The Distributed Map body: soft-deletes one window of staged entities with their edges.</summary>
    public const string DeleteChunk = "delete-chunk";

    /// <summary>Hands back the next wave, or an empty one once every window is done.</summary>
    public const string NextWave = "next-wave";

    /// <summary>Drops the scan's ledger and the run's staging, and closes the row.</summary>
    public const string Finalize = "finalize";

    /// <summary>Closes the run's row as failed, from the Catch path.</summary>
    public const string RecordFailure = "record-failure";
}
