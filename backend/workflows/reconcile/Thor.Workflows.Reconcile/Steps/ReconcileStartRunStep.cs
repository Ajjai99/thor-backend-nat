using Microsoft.Extensions.Logging;
using Thor.DataConnectionManager;
using Thor.DataLayer.Data;
using Thor.DataLayer.Repositories;
using Thor.Workflows.Abstractions;
using Thor.Workflows.Hosting;
using Thor.Workflows.Hosting.Composition;
using Thor.Workflows.Reconcile.Reconciliation;
using Thor.Workflows.Reconcile.Scoping;

namespace Thor.Workflows.Reconcile.Steps;

/// <summary>
/// Opens a scan's reconcile run: checks the guards (<see cref="SourceEligibility"/>), makes every decision
/// and every non-destructive write (<see cref="AbsencePlanner"/>), stages the deletions, and hands back wave
/// zero over them. Deciding before deleting anything is the point — the threshold has to see a whole
/// (source, entity type) before any of it is touched.
/// </summary>
public sealed class ReconcileStartRunStep : WorkflowStep<ReconcileRequest>
{
    private readonly ILoggerFactory _loggerFactory;
    private readonly ITenantConnectionManager _tenantConnectionManager;
    private readonly int _rowsPerChunk;
    private readonly int _chunksPerWave;

    /// <summary>Test seam — lets tests substitute the tenant-connection dependency and pin the dials.</summary>
    public ReconcileStartRunStep(ILoggerFactory loggerFactory, ITenantConnectionManager tenantConnectionManager, int rowsPerChunk, int chunksPerWave)
    {
        _loggerFactory = loggerFactory;
        _tenantConnectionManager = tenantConnectionManager;
        _rowsPerChunk = rowsPerChunk;
        _chunksPerWave = chunksPerWave;
    }

    public ReconcileStartRunStep()
        : this(
            LoggerFactory.Create(builder => builder.AddConsole().SetMinimumLevel(LogLevel.Information)),
            TenantConnectionManagerFactory.Build(),
            ReconcileSettings.RowsPerChunk(),
            ReconcileSettings.ChunksPerWave())
    {
    }

    protected override async Task<StepResult> ExecuteAsync(ReconcileRequest request, CancellationToken cancellationToken)
    {
        await using var db = await _tenantConnectionManager.GetTenantDbContextAsync(request.TenantId, cancellationToken);
        return StepResult.Completed(await RunAsync(db, request, cancellationToken));
    }

    public async Task<ReconcileWave> RunAsync(TenantDbContext db, ReconcileRequest request, CancellationToken cancellationToken = default)
    {
        // JSON with no scanId deserializes to Guid.Empty, which matches no ledger rows — and an empty ledger
        // is "everything is absent". Rejected before anything else happens.
        if (request.ScanId == Guid.Empty)
        {
            throw new InvalidOperationException("ReconcileRequest.ScanId is required; a run with no scan has no ledger to compare against.");
        }

        var logger = _loggerFactory.CreateLogger<ReconcileStartRunStep>();
        var (tenantId, scanId) = request;
        var runId = ReconcileSettings.RunId(tenantId, scanId, logger);

        var workflows = new WorkflowRepository(db);
        var workflowId = await new WorkflowLifecycle(workflows)
            .EnsureStartedAsync(scanId, scanManifestId: null, WorkflowTypes.Reconcile, WorkflowTriggers.StepFunctions, runId, cancellationToken);
        await workflows.ReopenAsync(workflowId, cancellationToken);

        var eligibility = await new SourceEligibility(db).EvaluateAsync(scanId, cancellationToken);
        if (!eligibility.LedgerPresent)
        {
            logger.LogWarning(
                "Scan {ScanId} has no seen-ledger, so nothing can be concluded about absence and nothing is swept. Expected for a scan promoted before the ledger existed or one that delivered nothing; otherwise its promotion never recorded what it saw.",
                scanId);
            return ReconcileWavePlanner.WaveAt(request, total: 0, waveIndex: 0, _rowsPerChunk, _chunksPerWave);
        }

        var staged = await new AbsencePlanner(db, runId, scanId, logger).PlanAsync(eligibility, cancellationToken);

        logger.LogInformation(
            "Reconcile run {RunId} for scan {ScanId}: {Sweepable} source(s) swept, {Skipped} skipped, {Staged} entit(ies) staged for deletion.",
            runId, scanId, eligibility.Sweepable.Count, eligibility.Skipped.Count, staged);

        return ReconcileWavePlanner.WaveAt(request, staged, waveIndex: 0, _rowsPerChunk, _chunksPerWave);
    }
}
