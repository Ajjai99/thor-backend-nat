using Microsoft.Extensions.Logging;
using Thor.DataConnectionManager;
using Thor.DataLayer.Data;
using Thor.DataLayer.Repositories;
using Thor.Workflows.Abstractions;
using Thor.Workflows.Hosting;
using Thor.Workflows.Hosting.Composition;

namespace Thor.Workflows.Reconcile.Steps;

/// <summary>
/// Closes the run's row as failed, from the state machine's Catch path — the one place that sees a chunk
/// killed by its timeout. Opens the row first if start-run died before it could, so every failed run has a
/// row saying why. The ledger and staging are kept: the execution can be redriven until the scan's graph
/// load starts. Failure here must not swallow the failure; the definition still ends the execution failed.
/// </summary>
public sealed class ReconcileRecordFailureStep : WorkflowStep<ReconcileFailureRequest>
{
    private const int MaxErrorLength = 2000;

    private readonly ILoggerFactory _loggerFactory;
    private readonly ITenantConnectionManager _tenantConnectionManager;

    /// <summary>Test seam — lets tests substitute the tenant-connection dependency the parameterless constructor builds for real.</summary>
    public ReconcileRecordFailureStep(ILoggerFactory loggerFactory, ITenantConnectionManager tenantConnectionManager)
    {
        _loggerFactory = loggerFactory;
        _tenantConnectionManager = tenantConnectionManager;
    }

    public ReconcileRecordFailureStep()
        : this(
            LoggerFactory.Create(builder => builder.AddConsole().SetMinimumLevel(LogLevel.Information)),
            TenantConnectionManagerFactory.Build())
    {
    }

    protected override async Task<StepResult> ExecuteAsync(ReconcileFailureRequest request, CancellationToken cancellationToken)
    {
        await using var db = await _tenantConnectionManager.GetTenantDbContextAsync(request.Request.TenantId, cancellationToken);
        await RunAsync(db, request, cancellationToken);
        return StepResult.None;
    }

    public async Task RunAsync(TenantDbContext db, ReconcileFailureRequest request, CancellationToken cancellationToken = default)
    {
        var logger = _loggerFactory.CreateLogger<ReconcileRecordFailureStep>();
        var (tenantId, scanId) = request.Request;
        var runId = ReconcileSettings.RunId(tenantId, scanId, logger);
        var error = Describe(request.Error);

        var lifecycle = new WorkflowLifecycle(new WorkflowRepository(db));
        await lifecycle.EnsureStartedAsync(scanId, scanManifestId: null, WorkflowTypes.Reconcile, WorkflowTriggers.StepFunctions, runId, cancellationToken);
        await lifecycle.MarkFailedAsync(scanManifestId: null, WorkflowTypes.Reconcile, error, runId, cancellationToken: cancellationToken);

        logger.LogError("Reconcile failed for scan {ScanId}: {Error}", scanId, error);
    }

    /// <summary>Truncated: a Lambda failure's Cause carries a serialized stack trace; the full one is in the execution history.</summary>
    private static string Describe(ReconcileFailureCause? cause)
    {
        if (cause is null)
        {
            return "Unknown failure: the state machine's Catch supplied no error.";
        }

        var text = $"{cause.Error ?? "Unknown"}: {cause.Cause ?? "(no cause)"}";
        return text.Length <= MaxErrorLength ? text : text[..MaxErrorLength];
    }
}
