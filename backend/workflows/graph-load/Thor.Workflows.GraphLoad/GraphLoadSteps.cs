namespace Thor.Workflows.GraphLoad;

/// <summary>The <c>THOR_STEP</c> values this module answers to — see <c>infra/src/modules/workflow/asl/graph-load.asl.json</c>.</summary>
public static class GraphLoadSteps
{
    public const string StartRun = "start-run";

    public const string WriteChunk = "write-chunk";

    public const string NextWave = "next-wave";

    public const string Finalize = "finalize";

    public const string RecordFailure = "record-failure";
}
