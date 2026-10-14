using System.Text.Json;
using Amazon.StepFunctions;
using Amazon.StepFunctions.Model;
using Microsoft.Extensions.Logging;
using Thor.ScanGate.Core;

namespace Thor.ScanGate.Function;

/// <summary>The reconcile state machine this Lambda starts.</summary>
public sealed record ReconcileStateMachineOptions(string StateMachineArn);

/// <summary>
/// Starts reconcile with the execution name <c>reconcile-{scanId}</c>. The gate is evaluated
/// concurrently and repeatedly for one scan; the pinned name is what makes exactly one of those starts
/// happen — Step Functions rejects the rest with <see cref="ExecutionAlreadyExistsException"/>.
/// </summary>
public sealed class StepFunctionsReconcileStarter(
    IAmazonStepFunctions stepFunctions, ReconcileStateMachineOptions options, ILogger<StepFunctionsReconcileStarter> logger) : IReconcileStarter
{
    public async Task<bool> StartAsync(Guid tenantId, Guid scanId, CancellationToken cancellationToken = default)
    {
        var input = JsonSerializer.Serialize(new { TenantId = tenantId, ScanId = scanId });

        try
        {
            await stepFunctions.StartExecutionAsync(new StartExecutionRequest
            {
                StateMachineArn = options.StateMachineArn,
                Name = $"reconcile-{scanId}",
                Input = input,
            }, cancellationToken);
        }
        catch (ExecutionAlreadyExistsException ex)
        {
            logger.LogInformation(ex, "Reconcile for scan {ScanId} was already started; nothing to do", scanId);
        }

        return true;
    }
}
