using Microsoft.Extensions.Logging;
using Thor.DataConnectionManager;
using Thor.DataLayer.Data;
using Thor.Workflows.Abstractions;
using Thor.Workflows.Hosting.Composition;
using Thor.Workflows.Reconcile.Reconciliation;
using Thor.Workflows.Reconcile.Scoping;

namespace Thor.Workflows.Reconcile.Steps;

/// <summary>
/// The Map body: soft-deletes one window of the run's staged entities with their edges
/// (<see cref="EntityDeleter"/>). Idempotent, so the Map's retry and a redrive of a failed execution
/// both re-run it safely.
/// </summary>
public sealed class ReconcileDeleteChunkStep : WorkflowStep<ReconcileChunkRequest>
{
    private readonly ILoggerFactory _loggerFactory;
    private readonly ITenantConnectionManager _tenantConnectionManager;

    /// <summary>Test seam — lets tests substitute the tenant-connection dependency the parameterless constructor builds for real.</summary>
    public ReconcileDeleteChunkStep(ILoggerFactory loggerFactory, ITenantConnectionManager tenantConnectionManager)
    {
        _loggerFactory = loggerFactory;
        _tenantConnectionManager = tenantConnectionManager;
    }

    public ReconcileDeleteChunkStep()
        : this(
            LoggerFactory.Create(builder => builder.AddConsole().SetMinimumLevel(LogLevel.Information)),
            TenantConnectionManagerFactory.Build())
    {
    }

    protected override async Task<StepResult> ExecuteAsync(ReconcileChunkRequest request, CancellationToken cancellationToken)
    {
        await using var db = await _tenantConnectionManager.GetTenantDbContextAsync(request.TenantId, cancellationToken);
        return StepResult.Completed(await RunAsync(db, request, cancellationToken));
    }

    public async Task<ReconcileChunkResult> RunAsync(TenantDbContext db, ReconcileChunkRequest request, CancellationToken cancellationToken = default)
    {
        var logger = _loggerFactory.CreateLogger<ReconcileDeleteChunkStep>();
        await SourceEligibility.EnsureGraphLoadNotStartedAsync(db, request.ScanId, cancellationToken);

        var runId = ReconcileSettings.RunId(request.TenantId, request.ScanId, logger);
        var (entities, edges) = await new EntityDeleter(db, runId, request.ScanId)
            .DeleteWindowAsync(request.FromSeq, request.ToSeq, cancellationToken);

        logger.LogInformation(
            "Reconcile run {RunId} deleted {EntityCount} entit(ies) and {EdgeCount} edge(s) for window ({FromSeq}, {ToSeq}].",
            runId, entities, edges, request.FromSeq, request.ToSeq);

        return new ReconcileChunkResult(request.FromSeq, request.ToSeq, entities, edges);
    }
}
