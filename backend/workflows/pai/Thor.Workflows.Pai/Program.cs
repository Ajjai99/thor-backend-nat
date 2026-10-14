using Microsoft.Extensions.Logging;
using Thor.Workflows.Abstractions;
using Thor.Workflows.Hosting;
using Thor.Workflows.Pai;
using Thor.Workflows.Pai.Steps;

using var loggerFactory = LoggerFactory.Create(builder => builder.AddConsole().SetMinimumLevel(LogLevel.Information));
var logger = loggerFactory.CreateLogger("Thor.Workflows.Pai");

var steps = new Dictionary<string, Func<IWorkflowStep>>
{
    // Opens the run's workflow row, seeds definitions, stages the targets to recompute and hands back the first wave. Runs once.
    [PaiSteps.StartRun] = () => new PaiStartRunStep(),

    // Walks the group layer down from every target, one level at a time. Loops until done.
    [PaiSteps.Expand] = () => new PaiExpandStep(),

    // The Map body, one invocation per chunk of staged group paths.
    [PaiSteps.Fanout] = () => new PaiFanoutStep(),

    // Reads what the wave just did and builds the next one, or ends the loop.
    [PaiSteps.NextWave] = () => new PaiNextWaveStep(),

    // Prunes routes the run did not re-prove and closes the run's workflow row.
    [PaiSteps.Finalize] = () => new PaiFinalizeStep(),

    // Closes the run's workflow row as failed, from the definition's Catch path.
    [PaiSteps.RecordFailure] = () => new PaiRecordFailureStep(),

    // Marks one chunk's paths failed so the run carries on without pruning their targets.
    [PaiSteps.RecordChunkFailure] = () => new PaiRecordChunkFailureStep(),
};

return await WorkflowEntryPoint.RunAsync(steps, logger);
