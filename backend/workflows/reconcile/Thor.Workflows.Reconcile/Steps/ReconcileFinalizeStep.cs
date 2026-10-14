using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;
using Thor.DataConnectionManager;
using Thor.DataLayer.Data;
using Thor.DataLayer.Models.Tenants;
using Thor.DataLayer.Repositories;
using Thor.Workflows.Abstractions;
using Thor.Workflows.Hosting;
using Thor.Workflows.Hosting.Composition;
using Thor.Workflows.Reconcile.Persistence;

namespace Thor.Workflows.Reconcile.Steps;

/// <summary>
/// Runs once, after the last wave: drops the scan's seen-ledger and the run's staging, and closes the row —
/// <see cref="ReconcileStatuses.CompletedWithProposals"/> when any deletion now awaits an administrator.
/// The ledger goes last on purpose: while it exists, a failed run can be redriven.
/// </summary>
public sealed class ReconcileFinalizeStep : WorkflowStep<ReconcileRequest>
{
    private readonly ILoggerFactory _loggerFactory;
    private readonly ITenantConnectionManager _tenantConnectionManager;

    /// <summary>Test seam — lets tests substitute the tenant-connection dependency the parameterless constructor builds for real.</summary>
    public ReconcileFinalizeStep(ILoggerFactory loggerFactory, ITenantConnectionManager tenantConnectionManager)
    {
        _loggerFactory = loggerFactory;
        _tenantConnectionManager = tenantConnectionManager;
    }

    public ReconcileFinalizeStep()
        : this(
            LoggerFactory.Create(builder => builder.AddConsole().SetMinimumLevel(LogLevel.Information)),
            TenantConnectionManagerFactory.Build())
    {
    }

    protected override async Task<StepResult> ExecuteAsync(ReconcileRequest request, CancellationToken cancellationToken)
    {
        await using var db = await _tenantConnectionManager.GetTenantDbContextAsync(request.TenantId, cancellationToken);
        await RunAsync(db, request, cancellationToken);
        return StepResult.None;
    }

    public async Task RunAsync(TenantDbContext db, ReconcileRequest request, CancellationToken cancellationToken = default)
    {
        var logger = _loggerFactory.CreateLogger<ReconcileFinalizeStep>();
        var (tenantId, scanId) = request;
        var runId = ReconcileSettings.RunId(tenantId, scanId, logger);

        var ledgerRows = await ReconcileSql.ExecAsync(db, "DELETE FROM tenant.scan_seen_entity WHERE scan_id = @scan",
            [new NpgsqlParameter("scan", scanId)], cancellationToken);
        await ReconcileSql.ExecAsync(db, "DELETE FROM tenant.reconcile_pending WHERE run_id = @run",
            [new NpgsqlParameter("run", runId)], cancellationToken);

        var proposed = await db.ReconcileDecisions
            .AnyAsync(d => d.RunId == runId && d.Outcome == ReconcileOutcome.Proposed, cancellationToken);

        await new WorkflowLifecycle(new WorkflowRepository(db)).MarkCompletedAsync(
            scanManifestId: null, WorkflowTypes.Reconcile, runId,
            proposed ? ReconcileStatuses.CompletedWithProposals : "completed", cancellationToken);

        logger.LogInformation(
            "Reconcile run {RunId} for scan {ScanId} complete{Proposals}; dropped {LedgerRows} ledger row(s).",
            runId, scanId, proposed ? " with deletions awaiting an administrator" : "", ledgerRows);
    }
}
