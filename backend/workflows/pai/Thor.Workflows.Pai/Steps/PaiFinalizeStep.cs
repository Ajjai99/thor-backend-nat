using Microsoft.Extensions.Logging;
using Thor.DataConnectionManager;
using Thor.DataLayer.Data;
using Thor.DataLayer.Repositories;
using Thor.Workflows.Abstractions;
using Thor.Workflows.Hosting;
using Thor.Workflows.Hosting.Composition;
using Thor.Workflows.Pai.Constants;
using Thor.Workflows.Pai.Persistence;

namespace Thor.Workflows.Pai.Steps;

/// <summary>
/// Runs once, after the last wave: prunes what the run did not re-prove (<see cref="PaiPruner"/>),
/// closes the <c>workflow</c> row, and drops the run's staging.
///
/// <para>Closes <see cref="PaiStatuses.CompletedWithErrors"/> when a chunk was tolerated — a non-empty
/// <c>workflow.error</c>, appended by <see cref="PaiRecordChunkFailureStep"/>. Only a clean
/// <see cref="PaiStatuses.Completed"/> covers the run's scans and advances its definitions' hashes, so
/// after a partial run the next one redoes the same change set, and any full definition stays full.</para>
///
/// <para>Prune, hash and status commit together: a crash between them must not leave a run marked
/// complete whose stale routes were never pruned, or the reverse.</para>
/// </summary>
public sealed class PaiFinalizeStep : WorkflowStep<PaiRequest>
{
    private readonly ILoggerFactory _loggerFactory;
    private readonly ITenantConnectionManager _tenantConnectionManager;

    /// <summary>Test seam — lets tests substitute the tenant connection the parameterless constructor builds for real.</summary>
    internal PaiFinalizeStep(ILoggerFactory loggerFactory, ITenantConnectionManager tenantConnectionManager)
    {
        _loggerFactory = loggerFactory;
        _tenantConnectionManager = tenantConnectionManager;
    }

    public PaiFinalizeStep()
        : this(
            LoggerFactory.Create(builder => builder.AddConsole().SetMinimumLevel(LogLevel.Information)),
            TenantConnectionManagerFactory.Build())
    {
    }

    protected override async Task<StepResult> ExecuteAsync(PaiRequest request, CancellationToken cancellationToken)
    {
        await using var db = await _tenantConnectionManager.GetTenantDbContextAsync(request.TenantId, cancellationToken);
        await RunAsync(db, request, cancellationToken);
        return StepResult.None;
    }

    internal async Task RunAsync(TenantDbContext db, PaiRequest request, CancellationToken cancellationToken = default)
    {
        var logger = _loggerFactory.CreateLogger<PaiFinalizeStep>();
        var runId = PaiRunIdentity.Derive(request, logger);
        var repository = new WorkflowRepository(db);

        var workflow = await repository.GetByRunIdAsync(runId, cancellationToken)
            ?? throw new InvalidOperationException($"No PAI workflow row for run {runId}.");
        var lostChunks = !string.IsNullOrEmpty(workflow.Error);

        PruneSummary pruned;
        await using (var transaction = await db.Database.BeginTransactionAsync(cancellationToken))
        {
            pruned = await PaiPruner.PruneAsync(db, runId, cancellationToken);
            if (!lostChunks)
            {
                await PaiPruner.AdvanceComputedHashesAsync(db, runId, cancellationToken);
            }

            await new WorkflowLifecycle(repository).MarkCompletedAsync(
                scanManifestId: null, WorkflowTypes.Pai, runId,
                lostChunks ? PaiStatuses.CompletedWithErrors : PaiStatuses.Completed, cancellationToken);

            await transaction.CommitAsync(cancellationToken);
        }

        await PaiPruner.DropStagingAsync(db, runId, cancellationToken);

        if (lostChunks)
        {
            logger.LogError(
                "PAI run {RunId} finished for scan {ScanId} with failed chunks; their targets were not pruned: {Error}. Pruned {@Pruned}.",
                runId, request.ScanId, workflow.Error, pruned);
        }
        else
        {
            logger.LogInformation("PAI run {RunId} complete for scan {ScanId}. Pruned {@Pruned}.", runId, request.ScanId, pruned);
        }
    }
}
