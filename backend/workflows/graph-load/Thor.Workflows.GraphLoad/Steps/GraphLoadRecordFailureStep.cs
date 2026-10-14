using Microsoft.Extensions.Logging;
using Thor.DataConnectionManager;
using Thor.DataLayer.Data;
using Thor.DataLayer.Repositories;
using Thor.Workflows.Abstractions;
using Thor.Workflows.Hosting;
using Thor.Workflows.Hosting.Composition;

namespace Thor.Workflows.GraphLoad.Steps;

/// <summary>
/// Closes the scan's graph-load row as failed, from the state machine's Catch path — the one place
/// that sees a chunk killed by the Lambda timeout or out of memory, which no step's own catch block
/// can record (the reasoning of ATRE's record-failure).
///
/// <para>Failure here must not swallow the failure: the definition catches this state straight to the
/// failed end, keeping the original error in the execution history.</para>
/// </summary>
public sealed class GraphLoadRecordFailureStep : WorkflowStep<GraphLoadFailureRequest>
{
    private const int MaxErrorLength = 2000;

    private readonly ILoggerFactory _loggerFactory;
    private readonly ITenantConnectionManager _tenantConnectionManager;

    /// <summary>Test seam — lets tests substitute the tenant-connection dependency the parameterless constructor builds for real.</summary>
    public GraphLoadRecordFailureStep(ILoggerFactory loggerFactory, ITenantConnectionManager tenantConnectionManager)
    {
        _loggerFactory = loggerFactory;
        _tenantConnectionManager = tenantConnectionManager;
    }

    public GraphLoadRecordFailureStep()
        : this(
            LoggerFactory.Create(builder => builder.AddConsole().SetMinimumLevel(LogLevel.Information)),
            TenantConnectionManagerFactory.Build())
    {
    }

    protected override async Task<StepResult> ExecuteAsync(GraphLoadFailureRequest request, CancellationToken cancellationToken)
    {
        await using var db = await _tenantConnectionManager.GetTenantDbContextAsync(request.Request.TenantId, cancellationToken);
        await RunAsync(db, request, cancellationToken);
        return StepResult.None;
    }

    public async Task RunAsync(TenantDbContext db, GraphLoadFailureRequest request, CancellationToken cancellationToken = default)
    {
        var logger = _loggerFactory.CreateLogger<GraphLoadRecordFailureStep>();
        var (tenantId, scanId) = request.Request;
        var error = Describe(request.Error);

        // No-op when start-run failed before opening the row — there is nothing to close.
        await new WorkflowLifecycle(new WorkflowRepository(db)).MarkFailedAsync(
            scanManifestId: null, WorkflowTypes.GraphLoad, error, GraphLoadSettings.RunId(tenantId, scanId, logger), cancellationToken: cancellationToken);

        logger.LogError("Graph load failed for scan {ScanId}: {Error}", scanId, error);
    }

    /// <summary>Truncated: a Lambda failure's Cause carries a serialized stack trace; the full one is in the execution history.</summary>
    private static string Describe(GraphLoadFailureCause? cause)
    {
        if (cause is null)
        {
            return "Unknown failure: the state machine's Catch supplied no error.";
        }

        var text = $"{cause.Error ?? "Unknown"}: {cause.Cause ?? "(no cause)"}";
        return text.Length <= MaxErrorLength ? text : text[..MaxErrorLength];
    }
}
