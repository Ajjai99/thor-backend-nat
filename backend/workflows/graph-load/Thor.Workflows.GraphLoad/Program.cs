using Microsoft.Extensions.Logging;
using Thor.Workflows.Abstractions;
using Thor.Workflows.GraphLoad;
using Thor.Workflows.GraphLoad.Steps;
using Thor.Workflows.Hosting;

using var loggerFactory = LoggerFactory.Create(builder => builder.AddConsole().SetMinimumLevel(LogLevel.Information));
var logger = loggerFactory.CreateLogger("Thor.Workflows.GraphLoad");

var steps = new Dictionary<string, Func<IWorkflowStep>>
{
    // Opens the scan's workflow row, lists what the scan changed and hands back the first wave. Runs once.
    [GraphLoadSteps.StartRun] = () => new GraphLoadStartRunStep(),

    // The Map body, one invocation per chunk of ids. Holds no run-level state of its own.
    [GraphLoadSteps.WriteChunk] = () => new GraphLoadWriteChunkStep(),

    // Reads the plan and builds the next wave, or ends the loop.
    [GraphLoadSteps.NextWave] = () => new GraphLoadNextWaveStep(),

    // Applies the scan's deletes and closes the row, after the last wave.
    [GraphLoadSteps.Finalize] = () => new GraphLoadFinalizeStep(),

    // Closes the row as failed, from the definition's Catch path.
    [GraphLoadSteps.RecordFailure] = () => new GraphLoadRecordFailureStep(),
};

return await WorkflowEntryPoint.RunAsync(steps, logger);
