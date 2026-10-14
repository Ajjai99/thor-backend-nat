using System.Text.Json;
using Amazon.S3;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Thor.DataConnectionManager;
using Thor.DataLayer.Data;
using Thor.DataLayer.Repositories;
using Thor.S3;
using Thor.Workflows.Abstractions;
using Thor.Workflows.GraphLoad.Persistence;
using Thor.Workflows.Hosting;
using Thor.Workflows.Hosting.Composition;

namespace Thor.Workflows.GraphLoad.Steps;

/// <summary>
/// Opens a scan's graph load: works out from Postgres everything the scan's successfully ingested
/// manifests changed (<see cref="GraphLoadWriteSet"/>), writes it as files of ids, and writes the plan
/// the waves follow. Hands back wave zero, as ATRE's start-run does.
///
/// <para>Only ids are read here; each chunk reads its own rows. That spreads the Postgres reads across
/// the wave instead of holding a whole scan's rows in one invocation, and keeps this step to one pass
/// over the scan's change events.</para>
///
/// <para>A manifest whose ingestion did not complete contributes nothing — ATRE and Ownership never ran
/// for it either.</para>
/// </summary>
public sealed class GraphLoadStartRunStep : WorkflowStep<GraphLoadRequest>
{
    private const string IngestionSucceededStatus = "completed";

    private readonly ILoggerFactory _loggerFactory;
    private readonly ITenantConnectionManager _tenantConnectionManager;
    private readonly IS3ObjectStore _s3;
    private readonly string _bucket;
    private readonly int _idsPerChunk;
    private readonly int _chunksPerWave;

    /// <summary>Test seam — lets tests substitute fakes for every external dependency the parameterless constructor builds for real.</summary>
    public GraphLoadStartRunStep(
        ILoggerFactory loggerFactory, ITenantConnectionManager tenantConnectionManager, IS3ObjectStore s3,
        string bucket, int idsPerChunk, int chunksPerWave)
    {
        _loggerFactory = loggerFactory;
        _tenantConnectionManager = tenantConnectionManager;
        _s3 = s3;
        _bucket = bucket;
        _idsPerChunk = idsPerChunk;
        _chunksPerWave = chunksPerWave;
    }

    public GraphLoadStartRunStep()
        : this(
            LoggerFactory.Create(builder => builder.AddConsole().SetMinimumLevel(LogLevel.Information)),
            TenantConnectionManagerFactory.Build(),
            new S3ObjectStore(new AmazonS3Client()),
            GraphLoadSettings.Bucket(),
            GraphLoadSettings.IdsPerChunk(),
            GraphLoadSettings.ChunksPerWave())
    {
    }

    protected override async Task<StepResult> ExecuteAsync(GraphLoadRequest request, CancellationToken cancellationToken)
    {
        await using var db = await _tenantConnectionManager.GetTenantDbContextAsync(request.TenantId, cancellationToken);
        return StepResult.Completed(await RunAsync(db, request, cancellationToken));
    }

    public async Task<GraphLoadWave> RunAsync(TenantDbContext db, GraphLoadRequest request, CancellationToken cancellationToken = default)
    {
        var logger = _loggerFactory.CreateLogger<GraphLoadStartRunStep>();
        var (tenantId, scanId) = request;
        var runId = GraphLoadSettings.RunId(tenantId, scanId, logger);

        var workflows = new WorkflowRepository(db);
        var workflowId = await new WorkflowLifecycle(workflows)
            .EnsureStartedAsync(scanId, scanManifestId: null, WorkflowTypes.GraphLoad, WorkflowTriggers.StepFunctions, runId, cancellationToken);
        // A fresh attempt at the scan's load: clear what a failed earlier one recorded.
        await workflows.ReopenAsync(workflowId, cancellationToken);

        var scanManifestIds = (await workflows.GetForManifestsOfScanAsync(scanId, cancellationToken))
            .Where(w => w.WorkflowType == WorkflowTypes.Ingestion && w.Status == IngestionSucceededStatus && w.ScanManifestId is not null)
            .Select(w => w.ScanManifestId!.Value)
            .Distinct()
            .ToList();

        var writeSet = new GraphLoadWriteSet(db, scanId, scanManifestIds);
        var vertexChunks = new List<GraphLoadChunk>();
        foreach (var kind in GraphLoadKinds.Vertices)
        {
            vertexChunks.AddRange(await WriteChunksAsync(tenantId, scanId, kind, writeSet.VertexIds(kind), cancellationToken));
        }
        var edgeChunks = await WriteChunksAsync(tenantId, scanId, GraphLoadKinds.Edge, writeSet.EdgeIds(), cancellationToken);

        // The commit point — only after every file it names is in place.
        var plan = new GraphLoadPlan(scanManifestIds, vertexChunks, edgeChunks);
        await _s3.PutObjectAsync(_bucket, GraphLoadKeys.Plan(tenantId, scanId), JsonSerializer.SerializeToUtf8Bytes(plan), cancellationToken);

        logger.LogInformation(
            "Graph load for scan {ScanId}: {ManifestCount} ingested manifest(s), {VertexChunks} vertex/{EdgeChunks} edge chunk(s) of up to {IdsPerChunk} id(s).",
            scanId, scanManifestIds.Count, vertexChunks.Count, edgeChunks.Count, _idsPerChunk);

        return GraphLoadWavePlanner.WaveAt(request, plan, 0, _chunksPerWave);
    }

    /// <summary>Streams the ids and writes a file each time a chunk fills, so no step holds a whole scan's ids at once.</summary>
    private async Task<List<GraphLoadChunk>> WriteChunksAsync(
        Guid tenantId, Guid scanId, string kind, IQueryable<Guid> ids, CancellationToken cancellationToken)
    {
        var chunks = new List<GraphLoadChunk>();
        var buffer = new List<Guid>(_idsPerChunk);

        async Task FlushAsync()
        {
            var key = GraphLoadKeys.Chunk(tenantId, scanId, kind, chunks.Count);
            await _s3.PutObjectAsync(_bucket, key, JsonSerializer.SerializeToUtf8Bytes(buffer), cancellationToken);
            chunks.Add(new GraphLoadChunk(kind, key));
            buffer.Clear();
        }

        await foreach (var id in ids.AsAsyncEnumerable().WithCancellation(cancellationToken))
        {
            buffer.Add(id);
            if (buffer.Count == _idsPerChunk)
            {
                await FlushAsync();
            }
        }

        if (buffer.Count > 0)
        {
            await FlushAsync();
        }

        return chunks;
    }
}
