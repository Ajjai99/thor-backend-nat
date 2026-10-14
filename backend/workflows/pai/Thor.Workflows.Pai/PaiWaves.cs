namespace Thor.Workflows.Pai;

/// <summary>
/// How much work a PAI run does at a time. Environment configuration because the right values depend
/// on an environment's Aurora and Neptune capacity — see <c>infra/src/workflow_definitions.tf</c>.
/// None of the defaults has been measured.
/// </summary>
public static class PaiWaves
{
    /// <summary>Staged group paths one Map item reads.</summary>
    public const string PathsPerChunkEnvVar = "THOR_PAI_PATHS_PER_CHUNK";

    /// <summary>Chunks handed to one Map. Pair it with the Map's MaxConcurrency.</summary>
    public const string ChunksPerWaveEnvVar = "THOR_PAI_CHUNKS_PER_WAVE";

    /// <summary>How long one expand invocation keeps starting new levels before handing back to the state machine.</summary>
    public const string ExpandBudgetSecondsEnvVar = "THOR_PAI_EXPAND_BUDGET_SECONDS";

    /// <summary>Most group paths staged for one target; beyond it the target's paths are truncated with a warning.</summary>
    public const string MaxPathsPerTargetEnvVar = "THOR_PAI_MAX_PATHS_PER_TARGET";

    /// <summary>
    /// A path's cost is its last group's member count, which varies by orders of magnitude, so this is
    /// a guess sized for groups in the low thousands.
    /// </summary>
    public const int DefaultPathsPerChunk = 500;

    /// <summary>Matches the Map's default MaxConcurrency, so a wave is exactly one full round of concurrent invocations.</summary>
    public const int DefaultChunksPerWave = 5;

    /// <summary>Ten minutes of a fifteen-minute Lambda timeout; a level is never cut short, so the rest is headroom for the one running.</summary>
    public const int DefaultExpandBudgetSeconds = 600;

    /// <summary>Generous for real nesting; its job is to stop a pathological diamond lattice exploding combinatorially.</summary>
    public const int DefaultMaxPathsPerTarget = 10_000;

    /// <summary>
    /// Deepest group path staged, and deepest an incremental run walks up from a changed group. Deep
    /// enough for any real directory; hitting it is logged, because routes beyond it are not found.
    /// </summary>
    public const int DepthCap = 10;

    public static int PathsPerChunkFromEnvironment() => PositiveEnvVar(PathsPerChunkEnvVar, DefaultPathsPerChunk);

    public static int ChunksPerWaveFromEnvironment() => PositiveEnvVar(ChunksPerWaveEnvVar, DefaultChunksPerWave);

    public static TimeSpan ExpandBudgetFromEnvironment() =>
        TimeSpan.FromSeconds(PositiveEnvVar(ExpandBudgetSecondsEnvVar, DefaultExpandBudgetSeconds));

    public static int MaxPathsPerTargetFromEnvironment() => PositiveEnvVar(MaxPathsPerTargetEnvVar, DefaultMaxPathsPerTarget);

    /// <summary>Unset, unparseable or non-positive all fall back to the default — a zero here would make a wave do nothing, forever.</summary>
    private static int PositiveEnvVar(string name, int fallback) =>
        int.TryParse(Environment.GetEnvironmentVariable(name), out var value) && value > 0 ? value : fallback;
}
