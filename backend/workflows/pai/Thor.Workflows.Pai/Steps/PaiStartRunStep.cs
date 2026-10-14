using Microsoft.Extensions.Logging;
using Thor.DataConnectionManager;
using Thor.DataLayer.Data;
using Thor.DataLayer.Repositories;
using Thor.Graph;
using Thor.Workflows.Abstractions;
using Thor.Workflows.Hosting;
using Thor.Workflows.Hosting.Composition;
using Thor.Workflows.Pai.Composition;
using Thor.Workflows.Pai.Models;
using Thor.Workflows.Pai.Persistence;
using Thor.Workflows.Pai.Privilege;
using Thor.Workflows.Pai.Seeding;

namespace Thor.Workflows.Pai.Steps;

/// <summary>
/// Runs once, before any expansion: validates the request, opens the run's <c>workflow</c> row, seeds
/// the definitions from config, stages what this run recomputes (<see cref="PaiSeedPlanner"/>) and
/// hands back the first fan-out wave. The wave's windows are only offsets, so it can be planned before
/// expand has staged the paths they will read.
/// </summary>
public sealed class PaiStartRunStep : WorkflowStep<PaiRequest>
{
    private readonly ILoggerFactory _loggerFactory;
    private readonly ITenantConnectionManager _tenantConnectionManager;
    private readonly IGraphReader _graph;
    private readonly Func<IReadOnlyList<ConfiguredPrivilegeDefinition>> _configured;
    private readonly int _pathsPerChunk;
    private readonly int _chunksPerWave;

    /// <summary>Test seam — lets tests substitute the tenant connection, the graph, the shipped definitions and the two dials.</summary>
    internal PaiStartRunStep(
        ILoggerFactory loggerFactory, ITenantConnectionManager tenantConnectionManager, IGraphReader graph,
        Func<IReadOnlyList<ConfiguredPrivilegeDefinition>> configured, int pathsPerChunk, int chunksPerWave)
    {
        _loggerFactory = loggerFactory;
        _tenantConnectionManager = tenantConnectionManager;
        _graph = graph;
        _configured = configured;
        _pathsPerChunk = pathsPerChunk;
        _chunksPerWave = chunksPerWave;
    }

    public PaiStartRunStep()
        : this(
            LoggerFactory.Create(builder => builder.AddConsole().SetMinimumLevel(LogLevel.Information)),
            TenantConnectionManagerFactory.Build(),
            PaiGraph.FromEnvironment(),
            () => new PrivilegeDefinitionProvider().GetAll(),
            PaiWaves.PathsPerChunkFromEnvironment(),
            PaiWaves.ChunksPerWaveFromEnvironment())
    {
    }

    protected override async Task<StepResult> ExecuteAsync(PaiRequest request, CancellationToken cancellationToken)
    {
        await using var db = await _tenantConnectionManager.GetTenantDbContextAsync(request.TenantId, cancellationToken);
        return StepResult.Completed(await RunAsync(db, request, cancellationToken));
    }

    internal async Task<PaiWave> RunAsync(TenantDbContext db, PaiRequest request, CancellationToken cancellationToken = default)
    {
        request.Validate();

        var logger = _loggerFactory.CreateLogger<PaiStartRunStep>();
        var runId = PaiRunIdentity.Derive(request, logger);
        var repository = new WorkflowRepository(db);
        var trigger = request.ScanId is not null ? WorkflowTriggers.StepFunctions : WorkflowTriggers.Api;

        var workflowId = await new WorkflowLifecycle(repository).EnsureStartedAsync(
            request.ScanId, scanManifestId: null, WorkflowTypes.Pai, trigger, runId, cancellationToken);

        // A retry reuses the row, still carrying the last attempt's error and completion; reset it so
        // this attempt's outcome is judged on its own (see IWorkflowRepository.ReopenAsync).
        await repository.ReopenAsync(workflowId, cancellationToken);

        await PrivilegeDefinitionSeeder.SeedAsync(db, _configured(), logger, cancellationToken);

        var summary = await new PaiSeedPlanner(db, _graph, request.TenantId, runId, logger).PlanAsync(request.ScanId, cancellationToken);

        logger.LogInformation(
            "PAI run {RunId} started for scan {ScanId}: {@Summary}; waves of {ChunksPerWave} x {PathsPerChunk} paths.",
            runId, request.ScanId, summary, _chunksPerWave, _pathsPerChunk);

        return PaiWavePlanner.WaveAt(request, waveIndex: 0, _pathsPerChunk, _chunksPerWave);
    }
}
