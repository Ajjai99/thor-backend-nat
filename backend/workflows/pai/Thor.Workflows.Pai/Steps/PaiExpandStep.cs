using Microsoft.Extensions.Logging;
using Thor.DataConnectionManager;
using Thor.DataLayer.Data;
using Thor.Graph;
using Thor.Workflows.Abstractions;
using Thor.Workflows.Hosting.Composition;
using Thor.Workflows.Pai.Composition;
using Thor.Workflows.Pai.Expansion;
using Thor.Workflows.Pai.Models;
using Thor.Workflows.Pai.Persistence;
using Thor.Workflows.Pai.Privilege;

namespace Thor.Workflows.Pai.Steps;

/// <summary>
/// Stages the run's group paths (<see cref="GroupPathExpander"/>). Shaped like a poll step: it stops
/// starting levels when its time budget is spent and answers <see cref="PaiExpandResult.IsInProgress"/>,
/// and the state machine invokes it again to carry on from the deepest staged level.
/// </summary>
public sealed class PaiExpandStep : WorkflowStep<PaiRequest>
{
    private readonly ILoggerFactory _loggerFactory;
    private readonly ITenantConnectionManager _tenantConnectionManager;
    private readonly IGraphReader _graph;
    private readonly TimeSpan _budget;
    private readonly int _maxPathsPerTarget;

    /// <summary>Test seam — lets tests substitute the tenant connection and the graph, and pin the budget and path cap.</summary>
    internal PaiExpandStep(
        ILoggerFactory loggerFactory, ITenantConnectionManager tenantConnectionManager, IGraphReader graph, TimeSpan budget, int maxPathsPerTarget)
    {
        _loggerFactory = loggerFactory;
        _tenantConnectionManager = tenantConnectionManager;
        _graph = graph;
        _budget = budget;
        _maxPathsPerTarget = maxPathsPerTarget;
    }

    public PaiExpandStep()
        : this(
            LoggerFactory.Create(builder => builder.AddConsole().SetMinimumLevel(LogLevel.Information)),
            TenantConnectionManagerFactory.Build(),
            PaiGraph.FromEnvironment(),
            PaiWaves.ExpandBudgetFromEnvironment(),
            PaiWaves.MaxPathsPerTargetFromEnvironment())
    {
    }

    protected override async Task<StepResult> ExecuteAsync(PaiRequest request, CancellationToken cancellationToken)
    {
        await using var db = await _tenantConnectionManager.GetTenantDbContextAsync(request.TenantId, cancellationToken);
        var result = await RunAsync(db, request, cancellationToken);

        // IsInProgress rides on the result too: the state machine's Choice reads it off the Lambda response.
        return result.IsInProgress ? StepResult.InProgress(result) : StepResult.Completed(result);
    }

    internal async Task<PaiExpandResult> RunAsync(TenantDbContext db, PaiRequest request, CancellationToken cancellationToken = default)
    {
        var logger = _loggerFactory.CreateLogger<PaiExpandStep>();
        var runId = PaiRunIdentity.Derive(request, logger);
        var definitions = await RunDefinition.LoadAsync(db, runId, cancellationToken);

        var progress = await new GroupPathExpander(db, _graph, request.TenantId, runId, _maxPathsPerTarget, logger)
            .RunAsync(definitions, _budget, cancellationToken);

        logger.LogInformation(
            "PAI run {RunId} expand: {Levels} level(s), {Paths} path(s) staged, complete: {IsComplete}.",
            runId, progress.LevelsRun, progress.PathsStaged, progress.IsComplete);

        return new PaiExpandResult(progress.LevelsRun, progress.PathsStaged, IsInProgress: !progress.IsComplete);
    }
}
