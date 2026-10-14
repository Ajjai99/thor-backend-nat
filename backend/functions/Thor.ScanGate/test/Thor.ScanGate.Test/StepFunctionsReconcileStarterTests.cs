using Amazon.StepFunctions;
using Amazon.StepFunctions.Model;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Thor.ScanGate.Function;

namespace Thor.ScanGate.Test;

public class StepFunctionsReconcileStarterTests
{
    private readonly IAmazonStepFunctions _stepFunctions = Substitute.For<IAmazonStepFunctions>();

    private StepFunctionsReconcileStarter CreateStarter() =>
        new(_stepFunctions, new ReconcileStateMachineOptions("arn:reconcile"), NullLogger<StepFunctionsReconcileStarter>.Instance);

    [Fact]
    public async Task StartAsync_PinsTheExecutionNameToTheScan()
    {
        var (tenantId, scanId) = (Guid.NewGuid(), Guid.NewGuid());

        Assert.True(await CreateStarter().StartAsync(tenantId, scanId));

        await _stepFunctions.Received(1).StartExecutionAsync(
            Arg.Is<StartExecutionRequest>(r =>
                r.StateMachineArn == "arn:reconcile" && r.Name == $"reconcile-{scanId}" && r.Input.Contains(tenantId.ToString()) && r.Input.Contains(scanId.ToString())),
            Arg.Any<CancellationToken>());
    }

    /// <summary>The gate runs concurrently and repeatedly per scan; only the first start may win.</summary>
    [Fact]
    public async Task StartAsync_AlreadyStarted_IsNotAnError()
    {
        _stepFunctions.StartExecutionAsync(Arg.Any<StartExecutionRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new ExecutionAlreadyExistsException("exists"));

        Assert.True(await CreateStarter().StartAsync(Guid.NewGuid(), Guid.NewGuid()));
    }

    [Fact]
    public async Task Disabled_StartsNothing_AndSaysThereIsNothingToWaitFor() =>
        Assert.False(await new DisabledReconcileStarter().StartAsync(Guid.NewGuid(), Guid.NewGuid()));
}
