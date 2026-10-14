using System.Text.Json;
using Amazon.S3;
using Microsoft.Extensions.Logging;
using Thor.DataConnectionManager;
using Thor.DataLayer.Data;
using Thor.DataLayer.Repositories;
using Thor.Graph;
using Thor.S3;
using Thor.Workflows.Abstractions;
using Thor.Workflows.GraphLoad.Persistence;
using Thor.Workflows.Hosting;
using Thor.Workflows.Hosting.Composition;

namespace Thor.Workflows.GraphLoad.Steps;

/// <summary>
/// Runs once, after the last wave: removes from the graph what the scan soft-deleted — vertices and
/// edges — and closes the scan's graph-load row. The state machine starts PAI after this.
///
/// <para>Deletes come last on purpose: applied only once every write of the scan has succeeded, so a
/// run that fails part-way leaves the graph with nothing removed — the recoverable state. They are taken
/// over the manifests the plan names, the same ones the writes covered.</para>
/// </summary>
public sealed class GraphLoadFinalizeStep : WorkflowStep<GraphLoadRequest>
{
    private readonly ILoggerFactory _loggerFactory;
    private readonly ITenantConnectionManager _tenantConnectionManager;
    private readonly IS3ObjectStore _s3;
    private readonly IGraphVertexStore _vertexStore;
    private readonly IGraphEdgeStore _edgeStore;
    private readonly string _bucket;

    /// <summary>Test seam — lets tests substitute fakes for every external dependency the parameterless constructor builds for real.</summary>
    public GraphLoadFinalizeStep(
        ILoggerFactory loggerFactory, ITenantConnectionManager tenantConnectionManager, IS3ObjectStore s3,
        IGraphVertexStore vertexStore, IGraphEdgeStore edgeStore, string bucket)
    {
        _loggerFactory = loggerFactory;
        _tenantConnectionManager = tenantConnectionManager;
        _s3 = s3;
        _vertexStore = vertexStore;
        _edgeStore = edgeStore;
        _bucket = bucket;
    }

    public GraphLoadFinalizeStep()
        : this(
            LoggerFactory.Create(builder => builder.AddConsole().SetMinimumLevel(LogLevel.Information)),
            TenantConnectionManagerFactory.Build(),
            new S3ObjectStore(new AmazonS3Client()),
            new GraphVertexStore(GraphLoadSettings.NeptuneFromEnvironment()),
            new GraphEdgeStore(GraphLoadSettings.NeptuneFromEnvironment()),
            GraphLoadSettings.Bucket())
    {
    }

    protected override async Task<StepResult> ExecuteAsync(GraphLoadRequest request, CancellationToken cancellationToken)
    {
        await using var db = await _tenantConnectionManager.GetTenantDbContextAsync(request.TenantId, cancellationToken);
        await RunAsync(db, request, cancellationToken);
        return StepResult.None;
    }

    public async Task RunAsync(TenantDbContext db, GraphLoadRequest request, CancellationToken cancellationToken = default)
    {
        var logger = _loggerFactory.CreateLogger<GraphLoadFinalizeStep>();
        var (tenantId, scanId) = request;

        var plan = JsonSerializer.Deserialize<GraphLoadPlan>(
                await _s3.GetObjectAsync(_bucket, GraphLoadKeys.Plan(tenantId, scanId), cancellationToken))
            ?? throw new InvalidOperationException($"Graph load plan for scan {scanId} is empty.");

        var writeSet = new GraphLoadWriteSet(db, scanId, plan.ScanManifestIds);
        var vertices = await writeSet.DeletedVerticesAsync(cancellationToken);
        var edges = await writeSet.DeletedEdgesAsync(cancellationToken);

        await GraphDeleteExecutor.DeleteVerticesAsync(_vertexStore, tenantId, vertices, cancellationToken);
        await GraphDeleteExecutor.DeleteEdgesAsync(
            _edgeStore, tenantId, [.. edges.Select(e => (e.RelType, new GraphVertexRef(e.FromType, e.FromId), new GraphVertexRef(e.ToType, e.ToId)))], cancellationToken);

        await new WorkflowLifecycle(new WorkflowRepository(db)).MarkCompletedAsync(
            scanManifestId: null, WorkflowTypes.GraphLoad, GraphLoadSettings.RunId(tenantId, scanId, logger), cancellationToken: cancellationToken);

        logger.LogInformation(
            "Graph load for scan {ScanId} complete: {VerticesDeleted} vertex/{EdgesDeleted} edge delete(s) applied.",
            scanId, vertices.Count, edges.Count);
    }
}
