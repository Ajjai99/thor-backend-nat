using Microsoft.Extensions.Logging;
using Thor.DataConnectionManager;
using Thor.DataLayer.Data;
using Thor.DataLayer.Repositories;
using Thor.Workflows.Abstractions;
using Thor.Workflows.Hosting;
using Thor.Workflows.Hosting.Composition;
using Thor.Workflows.Ingestion.Batching;
using Thor.Workflows.Ingestion.Constants;
using Thor.Workflows.Ingestion.EdgeGate;
using Thor.Workflows.Ingestion.Orchestration;

namespace Thor.Workflows.Ingestion.Steps;

/// <summary>
/// A manifest's last ingestion step: resolves its edges into <c>tenant.edge</c>, which ATRE and
/// Ownership read, and closes the manifest's ingestion row. Postgres only.
///
/// <para>It does not touch Neptune. The graph is written once per scan by the graph-load workflow,
/// which the scan-completion gate starts once every manifest's ingestion row is closed — this step's
/// <see cref="WorkflowLifecycle.MarkCompletedAsync"/> is what it waits on.</para>
/// </summary>
public sealed class ResolveEdgesStep : WorkflowStep<IngestionRequest>
{
    private readonly ILoggerFactory _loggerFactory;
    private readonly ITenantConnectionManager _tenantConnectionManager;
    private readonly IngestionBatchOptions _batch = IngestionBatchOptions.FromEnvironment();

    /// <summary>Test seam — lets tests substitute a fake tenant-routing dependency the parameterless constructor builds for real.</summary>
    public ResolveEdgesStep(ILoggerFactory loggerFactory, ITenantConnectionManager tenantConnectionManager)
    {
        _loggerFactory = loggerFactory;
        _tenantConnectionManager = tenantConnectionManager;
    }

    public ResolveEdgesStep()
        : this(LoggerFactory.Create(builder => builder.AddConsole().SetMinimumLevel(LogLevel.Information)), TenantConnectionManagerFactory.Build())
    {
    }

    protected override async Task<StepResult> ExecuteAsync(IngestionRequest request, CancellationToken cancellationToken)
    {
        await using var db = await _tenantConnectionManager.GetTenantDbContextAsync(request.TenantId, cancellationToken);
        await RunAsync(db, request.ScanId, request.ScanManifestId, request.RunId, cancellationToken);
        return StepResult.None;
    }

    public async Task RunAsync(TenantDbContext db, Guid scanId, Guid scanManifestId, Guid? runId = null, CancellationToken cancellationToken = default)
    {
        var workflowLifecycle = new WorkflowLifecycle(new WorkflowRepository(db));
        await workflowLifecycle.EnsureStartedAsync(scanId, scanManifestId, WorkflowTypes.Ingestion, WorkflowTriggers.StepFunctions, runId, cancellationToken);

        try
        {
            var edgeResolver = new EdgeResolver(new OneSidedRefIndex(), _loggerFactory.CreateLogger<EdgeResolver>(), _batch);
            var scanLifecycle = new ScanLifecycle(new ScanRepository(db), new ScanManifestRepository(db));

            var isInitial = await scanLifecycle.IsInitialScanAsync(scanId, cancellationToken);
            _ = isInitial
                ? await edgeResolver.ResolveFullAsync(db, scanId, scanManifestId, RelTypes.TwoSided, cancellationToken)
                : await edgeResolver.ResolveIncrementalAsync(db, scanId, scanManifestId, RelTypes.TwoSided, cancellationToken);

            await workflowLifecycle.MarkCompletedAsync(scanManifestId, WorkflowTypes.Ingestion, runId, cancellationToken: cancellationToken);
        }
        catch (Exception ex)
        {
            await workflowLifecycle.MarkFailedAsync(scanManifestId, WorkflowTypes.Ingestion, ex.Message, runId, IngestionStatuses.ResolveEdgesFailed, cancellationToken);
            throw;
        }
    }
}
