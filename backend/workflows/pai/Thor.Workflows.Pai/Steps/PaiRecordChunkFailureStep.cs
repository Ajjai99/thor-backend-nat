using Microsoft.Extensions.Logging;
using Npgsql;
using Thor.DataConnectionManager;
using Thor.DataLayer.Data;
using Thor.DataLayer.Repositories;
using Thor.Workflows.Abstractions;
using Thor.Workflows.Hosting.Composition;
using Thor.Workflows.Pai.Models;
using Thor.Workflows.Pai.Persistence;

namespace Thor.Workflows.Pai.Steps;

/// <summary>
/// Records one chunk's failure on the Map item's Catch path; the run then carries on. Marks the
/// window's staged paths failed, so finalize never prunes their targets — their routes were only partly
/// rewritten — and appends the window to <c>workflow.error</c>, which is what makes the run close
/// <c>completed_with_errors</c>. Both are single statements because a wave's chunks fail concurrently.
/// </summary>
public sealed class PaiRecordChunkFailureStep : WorkflowStep<PaiChunkFailureRequest>
{
    private const int MaxCauseLength = 500;

    private readonly ILoggerFactory _loggerFactory;
    private readonly ITenantConnectionManager _tenantConnectionManager;

    /// <summary>Test seam — lets tests substitute the tenant connection the parameterless constructor builds for real.</summary>
    internal PaiRecordChunkFailureStep(ILoggerFactory loggerFactory, ITenantConnectionManager tenantConnectionManager)
    {
        _loggerFactory = loggerFactory;
        _tenantConnectionManager = tenantConnectionManager;
    }

    public PaiRecordChunkFailureStep()
        : this(
            LoggerFactory.Create(builder => builder.AddConsole().SetMinimumLevel(LogLevel.Information)),
            TenantConnectionManagerFactory.Build())
    {
    }

    protected override async Task<StepResult> ExecuteAsync(PaiChunkFailureRequest request, CancellationToken cancellationToken)
    {
        await using var db = await _tenantConnectionManager.GetTenantDbContextAsync(request.TenantId, cancellationToken);
        await RunAsync(db, request, cancellationToken);
        return StepResult.None;
    }

    internal async Task RunAsync(TenantDbContext db, PaiChunkFailureRequest request, CancellationToken cancellationToken = default)
    {
        var logger = _loggerFactory.CreateLogger<PaiRecordChunkFailureStep>();
        var runId = PaiRunIdentity.Derive(new PaiRequest(request.TenantId, request.ScanId, request.RunId), logger);
        var message = $"Chunk [{request.Offset}, {request.Offset + request.Limit}) was not written: {PaiFailureCause.Describe(request.Error, MaxCauseLength)}";

        await PaiSql.ExecAsync(db, """
            UPDATE tenant.pai_group_path SET failed = TRUE
            WHERE id IN (SELECT id FROM tenant.pai_group_path WHERE run_id = @run ORDER BY id OFFSET @offset LIMIT @limit)
            """,
            [new NpgsqlParameter("run", runId), new NpgsqlParameter("offset", request.Offset), new NpgsqlParameter("limit", request.Limit)],
            cancellationToken);

        var repository = new WorkflowRepository(db);
        var workflow = await repository.GetByRunIdAsync(runId, cancellationToken);
        if (workflow is null)
        {
            // Loud: nothing will say the run lost a window. The paths are still marked, so nothing is wrongly pruned.
            logger.LogError("No PAI workflow row for run {RunId}; {Message}", runId, message);
            return;
        }

        await repository.AppendErrorAsync(workflow.Id, message, cancellationToken);
        logger.LogError("PAI run {RunId}: {Message}", runId, message);
    }
}
