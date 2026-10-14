using Microsoft.Extensions.Logging;
using Thor.DataConnectionManager;
using Thor.DataLayer.Data;
using Thor.Graph;
using Thor.Workflows.Abstractions;
using Thor.Workflows.Hosting.Composition;
using Thor.Workflows.Pai.Composition;
using Thor.Workflows.Pai.Fanout;
using Thor.Workflows.Pai.Models;
using Thor.Workflows.Pai.Persistence;
using Thor.Workflows.Pai.Privilege;

namespace Thor.Workflows.Pai.Steps;

/// <summary>
/// The Distributed Map body: writes the routes for one window of staged paths
/// (<see cref="RouteWriter"/>). Reads and writes through separate contexts, the arrangement ATRE and
/// Ownership use, which is why the Map's concurrency is capped. Does no workflow-row bookkeeping; a
/// throw fails only this item, and the state machine records it through
/// <see cref="PaiRecordChunkFailureStep"/>.
/// </summary>
public sealed class PaiFanoutStep : WorkflowStep<PaiChunkRequest>
{
    private readonly ILoggerFactory _loggerFactory;
    private readonly ITenantConnectionManager _tenantConnectionManager;
    private readonly IGraphReader _graph;

    /// <summary>Test seam — lets tests substitute the tenant connection and the graph.</summary>
    internal PaiFanoutStep(ILoggerFactory loggerFactory, ITenantConnectionManager tenantConnectionManager, IGraphReader graph)
    {
        _loggerFactory = loggerFactory;
        _tenantConnectionManager = tenantConnectionManager;
        _graph = graph;
    }

    public PaiFanoutStep()
        : this(
            LoggerFactory.Create(builder => builder.AddConsole().SetMinimumLevel(LogLevel.Information)),
            TenantConnectionManagerFactory.Build(),
            PaiGraph.FromEnvironment())
    {
    }

    protected override async Task<StepResult> ExecuteAsync(PaiChunkRequest request, CancellationToken cancellationToken)
    {
        try
        {
            await using var readContext = await _tenantConnectionManager.GetTenantDbContextAsync(request.TenantId, cancellationToken);
            await using var writeContext = await _tenantConnectionManager.GetTenantDbContextAsync(request.TenantId, cancellationToken);
            return StepResult.Completed(await RunAsync(readContext, writeContext, request, cancellationToken));
        }
        catch (Exception ex)
        {
            _loggerFactory.CreateLogger<PaiFanoutStep>().LogError(
                ex, "PAI chunk [{Offset}, {End}) failed for tenant {TenantId}.", request.Offset, request.Offset + request.Limit, request.TenantId);
            throw;
        }
    }

    internal async Task<PaiChunkSummary> RunAsync(
        TenantDbContext readContext, TenantDbContext writeContext, PaiChunkRequest request, CancellationToken cancellationToken = default)
    {
        var logger = _loggerFactory.CreateLogger<PaiFanoutStep>();
        var runId = PaiRunIdentity.Derive(new PaiRequest(request.TenantId, request.ScanId, request.RunId), logger);
        var definitions = await RunDefinition.LoadAsync(readContext, runId, cancellationToken);

        var progress = await new RouteWriter(readContext, writeContext, _graph, request.TenantId, runId, logger)
            .WriteAsync(definitions, request.Offset, request.Limit, cancellationToken);

        return new PaiChunkSummary(runId, request.Offset, progress.PathsRead, progress.RoutesWritten);
    }
}
