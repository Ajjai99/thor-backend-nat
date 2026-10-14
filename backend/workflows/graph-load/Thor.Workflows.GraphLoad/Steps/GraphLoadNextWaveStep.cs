using System.Text.Json;
using Amazon.S3;
using Thor.S3;
using Thor.Workflows.Abstractions;

namespace Thor.Workflows.GraphLoad.Steps;

/// <summary>
/// The bottom of the wave loop: reads the scan's plan and hands back the next wave, or an empty one
/// with <see cref="GraphLoadWave.HasMore"/> false once every chunk has been written. Unlike ATRE's
/// next-wave it reads S3 — the plan is a list of keys, not arithmetic — but it touches no database.
/// </summary>
public sealed class GraphLoadNextWaveStep : WorkflowStep<GraphLoadNextWaveRequest>
{
    private readonly IS3ObjectStore _s3;
    private readonly string _bucket;
    private readonly int _chunksPerWave;

    /// <summary>Test seam — lets tests substitute the S3 store and pin the dial the parameterless constructor reads from the environment.</summary>
    public GraphLoadNextWaveStep(IS3ObjectStore s3, string bucket, int chunksPerWave)
    {
        _s3 = s3;
        _bucket = bucket;
        _chunksPerWave = chunksPerWave;
    }

    public GraphLoadNextWaveStep()
        : this(new S3ObjectStore(new AmazonS3Client()), GraphLoadSettings.Bucket(), GraphLoadSettings.ChunksPerWave())
    {
    }

    protected override async Task<StepResult> ExecuteAsync(GraphLoadNextWaveRequest request, CancellationToken cancellationToken) =>
        StepResult.Completed(await RunAsync(request, cancellationToken));

    public async Task<GraphLoadWave> RunAsync(GraphLoadNextWaveRequest request, CancellationToken cancellationToken = default)
    {
        var (tenantId, scanId) = request.Request;
        var plan = JsonSerializer.Deserialize<GraphLoadPlan>(
                await _s3.GetObjectAsync(_bucket, GraphLoadKeys.Plan(tenantId, scanId), cancellationToken))
            ?? throw new InvalidOperationException($"Graph load plan for scan {scanId} is empty.");

        return GraphLoadWavePlanner.WaveAt(request.Request, plan, request.WaveIndex + 1, _chunksPerWave);
    }
}
