using System.Text.Json;
using Amazon.StepFunctions;
using Amazon.StepFunctions.Model;
using Microsoft.Extensions.Logging;
using Thor.ScanGate.Core;

namespace Thor.ScanGate.Function;

/// <summary>The PAI state machine this Lambda starts.</summary>
public sealed record PaiStateMachineOptions(string StateMachineArn);

/// <summary>
/// Starts PAI with the execution name <c>pai-{scanId}</c>. The gate is evaluated concurrently and
/// repeatedly for one scan; the pinned name is what makes exactly one of those starts happen —
/// Step Functions rejects the rest with <see cref="ExecutionAlreadyExistsException"/>.
/// </summary>
public sealed class StepFunctionsPaiStarter(
    IAmazonStepFunctions stepFunctions, PaiStateMachineOptions options, ILogger<StepFunctionsPaiStarter> logger) : IPaiStarter
{
    public async Task StartAsync(Guid tenantId, Guid scanId, CancellationToken cancellationToken = default)
    {
        var input = JsonSerializer.Serialize(new { TenantId = tenantId, ScanId = scanId });

        try
        {
            await stepFunctions.StartExecutionAsync(new StartExecutionRequest
            {
                StateMachineArn = options.StateMachineArn,
                Name = $"pai-{scanId}",
                Input = input,
            }, cancellationToken);
        }
        catch (ExecutionAlreadyExistsException ex)
        {
            logger.LogInformation(ex, "PAI for scan {ScanId} was already started; nothing to do", scanId);
        }
    }
}
