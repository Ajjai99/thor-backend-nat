using Microsoft.Extensions.Logging;
using Thor.Workflows.Hosting;

namespace Thor.Workflows.Reconcile;

/// <summary>
/// The run's dials, environment configuration because the right values depend on an environment's
/// Aurora capacity — see <c>infra/src/workflow_definitions.tf</c>. Unset, unparseable or non-positive
/// values fall back to the defaults. None has been measured.
/// </summary>
internal static class ReconcileSettings
{
    /// <summary>Staged entities one Map item deletes.</summary>
    public const string RowsPerChunkEnvVar = "THOR_RECONCILE_ROWS_PER_CHUNK";

    /// <summary>Chunks in one wave. Keep it equal to the Map's MaxConcurrency, or a wave leaves concurrency idle.</summary>
    public const string ChunksPerWaveEnvVar = "THOR_RECONCILE_CHUNKS_PER_WAVE";

    /// <summary>
    /// An entity's cost is its edge count, which varies by orders of magnitude, so this is a guess sized
    /// for accounts in tens of groups: a few hundred thousand edge updates per chunk at most.
    /// </summary>
    public const int DefaultRowsPerChunk = 5_000;

    public const int DefaultChunksPerWave = 4;

    /// <summary>
    /// Fixed namespace GUID for this module's deterministic run ids. Never change once shipped, and never
    /// share with another module: <c>WorkflowRepository.GetByRunIdAsync</c> is not filtered by workflow
    /// type, so two modules deriving from the same scan with one namespace would find each other's row.
    /// </summary>
    private static readonly Guid RunIdNamespace = Guid.Parse("d7c81b45-3a6f-4e9c-b2d0-5f14e8a36c92");

    public static int RowsPerChunk() => PositiveEnvVar(RowsPerChunkEnvVar, DefaultRowsPerChunk);

    public static int ChunksPerWave() => PositiveEnvVar(ChunksPerWaveEnvVar, DefaultChunksPerWave);

    /// <summary>One run per scan: a retry or redrive finds the row the first attempt opened.</summary>
    public static Guid RunId(Guid tenantId, Guid scanId, ILogger logger) =>
        DeterministicRunId.Derive(tenantId, scanId, explicitRunId: null, RunIdNamespace, logger);

    private static int PositiveEnvVar(string name, int fallback) =>
        int.TryParse(Environment.GetEnvironmentVariable(name), out var value) && value > 0 ? value : fallback;
}
