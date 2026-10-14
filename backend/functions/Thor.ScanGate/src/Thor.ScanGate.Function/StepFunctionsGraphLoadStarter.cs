using System.Text.Json;
using Amazon.StepFunctions;
using Amazon.StepFunctions.Model;
using Microsoft.Extensions.Logging;
using Thor.ScanGate.Core;

namespace Thor.ScanGate.Function;

public sealed record GraphLoadStateMachineOptions(string StateMachineArn);

/// <summary>
/// Starts the scan's graph-load workflow under the execution name <c>graph-load-{scanId}</c>. The
/// pinned name is what makes exactly one of the gate's many concurrent starts happen: Step Functions
/// rejects every later one with <see cref="ExecutionAlreadyExistsException"/>, which is success here.
/// </summary>
public sealed class StepFunctionsGraphLoadStarter(
    IAmazonStepFunctions stepFunctions, GraphLoadStateMachineOptions options, ILogger<StepFunctionsGraphLoadStarter> logger) : IGraphLoadStarter
{
    public async Task StartAsync(Guid tenantId, Guid scanId, CancellationToken cancellationToken = default)
    {
        try
        {
            await stepFunctions.StartExecutionAsync(new StartExecutionRequest
            {
                StateMachineArn = options.StateMachineArn,
                Name = $"graph-load-{scanId}",
                Input = JsonSerializer.Serialize(new { TenantId = tenantId, ScanId = scanId }),
            }, cancellationToken);
            logger.LogInformation("Started the graph load for scan {ScanId}", scanId);
        }
        catch (ExecutionAlreadyExistsException)
        {
            // Every evaluation after the first lands here.
        }
    }
}
