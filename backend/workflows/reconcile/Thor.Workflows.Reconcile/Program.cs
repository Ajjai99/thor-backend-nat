using Microsoft.Extensions.Logging;
using Thor.Workflows.Abstractions;
using Thor.Workflows.Hosting;
using Thor.Workflows.Reconcile;
using Thor.Workflows.Reconcile.Steps;

using var loggerFactory = LoggerFactory.Create(builder => builder.AddConsole().SetMinimumLevel(LogLevel.Information));
var logger = loggerFactory.CreateLogger("Thor.Workflows.Reconcile");

var steps = new Dictionary<string, Func<IWorkflowStep>>
{
    // Checks the guards, decides per (source, entity type), proposes or stages the deletions and hands back wave zero. Runs once.
    [ReconcileSteps.StartRun] = () => new ReconcileStartRunStep(),

    // The Map body: soft-deletes one window of staged entities with their edges.
    [ReconcileSteps.DeleteChunk] = () => new ReconcileDeleteChunkStep(),

    // Hands back the next wave, or ends the loop.
    [ReconcileSteps.NextWave] = () => new ReconcileNextWaveStep(),

    // Drops the scan's ledger and the run's staging, and closes the row.
    [ReconcileSteps.Finalize] = () => new ReconcileFinalizeStep(),

    // Closes the row as failed, from the definition's Catch path.
    [ReconcileSteps.RecordFailure] = () => new ReconcileRecordFailureStep(),
};

return await WorkflowEntryPoint.RunAsync(steps, logger);
