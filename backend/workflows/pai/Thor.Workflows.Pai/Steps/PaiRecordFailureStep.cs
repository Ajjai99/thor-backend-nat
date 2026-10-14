using Microsoft.Extensions.Logging;
using Thor.DataConnectionManager;
using Thor.DataLayer.Data;
using Thor.DataLayer.Repositories;
using Thor.Workflows.Abstractions;
using Thor.Workflows.Hosting;
using Thor.Workflows.Hosting.Composition;
using Thor.Workflows.Pai.Models;
using Thor.Workflows.Pai.Persistence;

namespace Thor.Workflows.Pai.Steps;

/// <summary>
/// Closes the run's <c>workflow</c> row as failed, from the definition's Catch path — the one place
/// that sees a Lambda killed by its timeout or memory, which runs no catch block. The run's staging is
/// kept, and its scans stay uncovered, so the next run picks their changes up.
/// </summary>
public sealed class PaiRecordFailureStep : WorkflowStep<PaiFailureRequest>
{
    private const int MaxErrorLength = 2000;

    private readonly ILoggerFactory _loggerFactory;
    private readonly ITenantConnectionManager _tenantConnectionManager;

    /// <summary>Test seam — lets tests substitute the tenant connection the parameterless constructor builds for real.</summary>
    internal PaiRecordFailureStep(ILoggerFactory loggerFactory, ITenantConnectionManager tenantConnectionManager)
    {
        _loggerFactory = loggerFactory;
        _tenantConnectionManager = tenantConnectionManager;
    }

    public PaiRecordFailureStep()
        : this(
            LoggerFactory.Create(builder => builder.AddConsole().SetMinimumLevel(LogLevel.Information)),
            TenantConnectionManagerFactory.Build())
    {
    }

    protected override async Task<StepResult> ExecuteAsync(PaiFailureRequest request, CancellationToken cancellationToken)
    {
        await using var db = await _tenantConnectionManager.GetTenantDbContextAsync(request.Request.TenantId, cancellationToken);
        await RunAsync(db, request, cancellationToken);
        return StepResult.None;
    }

    internal async Task RunAsync(TenantDbContext db, PaiFailureRequest request, CancellationToken cancellationToken = default)
    {
        var logger = _loggerFactory.CreateLogger<PaiRecordFailureStep>();
        var runId = PaiRunIdentity.Derive(request.Request, logger);
        var error = PaiFailureCause.Describe(request.Error, MaxErrorLength);

        // No-ops when there is no row: start-run failed before opening one.
        await new WorkflowLifecycle(new WorkflowRepository(db))
            .MarkFailedAsync(scanManifestId: null, WorkflowTypes.Pai, error, runId, cancellationToken: cancellationToken);

        logger.LogError("PAI run {RunId} failed for scan {ScanId}: {Error}", runId, request.Request.ScanId, error);
    }
}
