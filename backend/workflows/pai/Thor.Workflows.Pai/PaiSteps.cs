namespace Thor.Workflows.Pai;

/// <summary>
/// The <c>THOR_STEP</c> values this module answers to, named once so <c>Program.cs</c> and the
/// <c>steps</c> map in <c>infra/src/workflow_definitions.tf</c> cannot disagree silently.
/// </summary>
public static class PaiSteps
{
    /// <summary>Opens the run's workflow row, seeds definitions, stages this run's targets and hands back the first wave. Runs once.</summary>
    public const string StartRun = "start-run";

    /// <summary>Stages the simple paths down the group layer from every target, a level at a time. Loops until done.</summary>
    public const string Expand = "expand";

    /// <summary>The Distributed Map body: joins one window of staged paths to their direct account members and writes the routes.</summary>
    public const string Fanout = "fanout";

    /// <summary>Reads what the wave just did and builds the next one, or says there is none.</summary>
    public const string NextWave = "next-wave";

    /// <summary>Prunes the routes this run did not re-prove, closes the workflow row and drops the run's staging. Runs once.</summary>
    public const string Finalize = "finalize";

    /// <summary>Closes the run's workflow row as failed. On the Catch path, when the run itself dies.</summary>
    public const string RecordFailure = "record-failure";

    /// <summary>Records one tolerated chunk against the run, which then keeps going.</summary>
    public const string RecordChunkFailure = "record-chunk-failure";
}
