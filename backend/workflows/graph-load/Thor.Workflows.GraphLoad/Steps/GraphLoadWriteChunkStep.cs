using System.Text.Json;
using Amazon.S3;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Thor.DataConnectionManager;
using Thor.DataLayer.Data;
using Thor.Graph;
using Thor.S3;
using Thor.Workflows.Abstractions;
using Thor.Workflows.GraphLoad.Persistence;
using Thor.Workflows.Hosting.Composition;

namespace Thor.Workflows.GraphLoad.Steps;

/// <summary>
/// The wave's Map body: reads one chunk's rows from Postgres by id and writes them into Neptune as
/// batched upserts. One tenant database connection per invocation, so the Map's MaxConcurrency also
/// bounds this run's connections through the RDS proxy.
///
/// <para>Safe to run again. Every element is matched by its id, so a retried chunk (the Map retries,
/// and a redriven execution re-runs a whole wave) finds what an earlier attempt wrote instead of
/// adding to it. A chunk that still fails after its retries fails the run: a vertex chunk lost here
/// would fail every edge chunk that touches it, so tolerating it would trade one clear failure for many
/// unclear ones.</para>
///
/// <para>Rows are re-checked as live here, since one can be soft-deleted between start-run listing it
/// and this chunk reading it; such a row is skipped, and the scan that deleted it removes it from the
/// graph.</para>
/// </summary>
public sealed class GraphLoadWriteChunkStep : WorkflowStep<GraphLoadChunkRequest>
{
    private readonly ILoggerFactory _loggerFactory;
    private readonly ITenantConnectionManager _tenantConnectionManager;
    private readonly IS3ObjectStore _s3;
    private readonly IGraphBatchWriter _writer;
    private readonly string _bucket;

    /// <summary>Test seam — lets tests substitute fakes for every external dependency the parameterless constructor builds for real.</summary>
    public GraphLoadWriteChunkStep(
        ILoggerFactory loggerFactory, ITenantConnectionManager tenantConnectionManager, IS3ObjectStore s3, IGraphBatchWriter writer, string bucket)
    {
        _loggerFactory = loggerFactory;
        _tenantConnectionManager = tenantConnectionManager;
        _s3 = s3;
        _writer = writer;
        _bucket = bucket;
    }

    public GraphLoadWriteChunkStep()
        : this(
            LoggerFactory.Create(builder => builder.AddConsole().SetMinimumLevel(LogLevel.Information)),
            TenantConnectionManagerFactory.Build(),
            new S3ObjectStore(new AmazonS3Client()),
            new GraphBatchWriter(GraphLoadSettings.NeptuneFromEnvironment(), GraphLoadSettings.WriterOptions()),
            GraphLoadSettings.Bucket())
    {
    }

    protected override async Task<StepResult> ExecuteAsync(GraphLoadChunkRequest request, CancellationToken cancellationToken)
    {
        await using var db = await _tenantConnectionManager.GetTenantDbContextAsync(request.TenantId, cancellationToken);
        return StepResult.Completed(await RunAsync(db, request, cancellationToken));
    }

    public async Task<GraphLoadChunkResult> RunAsync(TenantDbContext db, GraphLoadChunkRequest request, CancellationToken cancellationToken = default)
    {
        var logger = _loggerFactory.CreateLogger<GraphLoadWriteChunkStep>();
        GraphLoadKeys.EnsureBelongsTo(request.Key, request.TenantId, request.ScanId);

        var ids = JsonSerializer.Deserialize<List<Guid>>(await _s3.GetObjectAsync(_bucket, request.Key, cancellationToken))
            ?? throw new InvalidOperationException($"Graph load chunk '{request.Key}' is empty.");

        var stats = request.Kind == GraphLoadKinds.Edge
            ? await _writer.UpsertEdgesAsync(request.TenantId, await LoadEdgesAsync(db, ids, cancellationToken), cancellationToken)
            : await _writer.UpsertVerticesAsync(request.TenantId, await LoadVerticesAsync(db, request.Kind, ids, cancellationToken), cancellationToken);

        logger.LogInformation(
            "Graph load chunk {Key} ({Kind}): {Ids} id(s), {Elements} element(s) written in {Requests} request(s), {Retries} retried.",
            request.Key, request.Kind, ids.Count, stats.Elements, stats.Requests, stats.Retries);

        return new GraphLoadChunkResult(request.Key, request.Kind, stats.Elements, stats.Requests, stats.Retries);
    }

    private static async Task<IReadOnlyList<GraphVertexUpsert>> LoadVerticesAsync(
        TenantDbContext db, string kind, List<Guid> ids, CancellationToken cancellationToken) =>
        kind switch
        {
            GraphLoadKinds.Account => (await db.Accounts.AsNoTracking().Where(a => ids.Contains(a.Id) && !a.IsDeleted).ToListAsync(cancellationToken))
                .Select(GraphVertexProjection.ToUpsert).ToList(),
            GraphLoadKinds.Group => (await db.Grps.AsNoTracking().Where(g => ids.Contains(g.Id) && !g.IsDeleted).ToListAsync(cancellationToken))
                .Select(GraphVertexProjection.ToUpsert).ToList(),
            GraphLoadKinds.Asset => (await db.Assets.AsNoTracking().Where(a => ids.Contains(a.Id)).ToListAsync(cancellationToken))
                .Select(GraphVertexProjection.ToUpsert).ToList(),
            _ => throw new InvalidOperationException($"Unknown graph load kind '{kind}'."),
        };

    /// <summary>Kept in target order, as start-run listed them, so one group's members stay together.</summary>
    private static async Task<IReadOnlyList<GraphEdgeUpsert>> LoadEdgesAsync(TenantDbContext db, List<Guid> ids, CancellationToken cancellationToken) =>
        (await db.Edges.AsNoTracking().Where(e => ids.Contains(e.Id) && !e.IsDeleted).ToListAsync(cancellationToken))
            .OrderBy(e => e.ToId).ThenBy(e => e.Id)
            .Select(e => new GraphEdgeUpsert(
                e.Id, e.RelType, new GraphVertexRef(e.FromType, e.FromId), new GraphVertexRef(e.ToType, e.ToId),
                EdgePropsConverter.ToPropertyDictionary(e.Props)))
            .ToList();
}
