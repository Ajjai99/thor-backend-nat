using Microsoft.Extensions.Logging;
using Thor.Graph;
using Thor.Workflows.Hosting;

namespace Thor.Workflows.GraphLoad;

/// <summary>
/// The graph load's dials, environment configuration because the right values depend on an
/// environment's Neptune and Aurora capacity rather than on anything about the code — see
/// <c>infra/src/workflow_definitions.tf</c>. Unset, unparseable or non-positive values fall back to
/// the defaults.
/// </summary>
internal static class GraphLoadSettings
{
    /// <summary>Bucket the plan and id files live in.</summary>
    public const string BucketEnvVar = "THOR_GRAPH_LOAD_BUCKET";

    /// <summary>Ids in one chunk file, and so one Map item's work.</summary>
    public const string IdsPerChunkEnvVar = "THOR_GRAPH_LOAD_IDS_PER_CHUNK";

    /// <summary>Chunks in one wave. Keep it equal to the Map's MaxConcurrency, or a wave leaves concurrency idle.</summary>
    public const string ChunksPerWaveEnvVar = "THOR_GRAPH_LOAD_CHUNKS_PER_WAVE";

    /// <summary>Requests one chunk keeps in flight. Times the Map's MaxConcurrency, this is one scan's share of the writer.</summary>
    public const string ParallelismEnvVar = "THOR_GRAPH_LOAD_WRITE_PARALLELISM";

    /// <summary>
    /// Sized so one chunk finishes well inside the Lambda timeout even on a busy writer. Unmeasured
    /// against Neptune: at 25 vertices a request and four requests in flight, 10,000 elements is about
    /// 100 round trips per connection.
    /// </summary>
    public const int DefaultIdsPerChunk = 10_000;

    public const int DefaultChunksPerWave = 8;

    public const int DefaultParallelism = 4;

    /// <summary>Fixed namespace GUID for the graph load's deterministic run ids. Never change once shipped, and never share with another module.</summary>
    private static readonly Guid RunIdNamespace = Guid.Parse("3b8d5f0e-7c41-4f6a-9e2d-1a6c8b4e9f73");

    public static string Bucket() =>
        Environment.GetEnvironmentVariable(BucketEnvVar) ?? throw new InvalidOperationException($"{BucketEnvVar} is required.");

    public static int IdsPerChunk() => PositiveEnvVar(IdsPerChunkEnvVar, DefaultIdsPerChunk);

    public static int ChunksPerWave() => PositiveEnvVar(ChunksPerWaveEnvVar, DefaultChunksPerWave);

    public static GraphBatchWriterOptions WriterOptions() => new() { Parallelism = PositiveEnvVar(ParallelismEnvVar, DefaultParallelism) };

    /// <summary>One run per scan: a retry finds the row the first attempt opened.</summary>
    public static Guid RunId(Guid tenantId, Guid scanId, ILogger logger) =>
        DeterministicRunId.Derive(tenantId, scanId, explicitRunId: null, RunIdNamespace, logger);

    public static NeptuneOptions NeptuneFromEnvironment()
    {
        var region = Environment.GetEnvironmentVariable("THOR_AWS_REGION") ?? Environment.GetEnvironmentVariable("AWS_REGION")
            ?? throw new InvalidOperationException("THOR_AWS_REGION or AWS_REGION is required.");
        return new NeptuneOptions
        {
            Endpoint = Environment.GetEnvironmentVariable("THOR_NEPTUNE_ENDPOINT") ?? "localhost",
            Port = int.TryParse(Environment.GetEnvironmentVariable("THOR_NEPTUNE_PORT"), out var port) ? port : 8182,
            EnableSsl = bool.TryParse(Environment.GetEnvironmentVariable("THOR_NEPTUNE_ENABLESSL"), out var ssl) ? ssl : true,
            Region = region,
        };
    }

    private static int PositiveEnvVar(string name, int fallback) =>
        int.TryParse(Environment.GetEnvironmentVariable(name), out var value) && value > 0 ? value : fallback;
}
