using System.Text.Json;
using Amazon.StepFunctions;
using Amazon.StepFunctions.Model;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Thor.ScanGate.Function;

namespace Thor.ScanGate.Test;

public class StepFunctionsGraphLoadStarterTests
{
    private readonly IAmazonStepFunctions _stepFunctions = Substitute.For<IAmazonStepFunctions>();

    private StepFunctionsGraphLoadStarter CreateStarter() =>
        new(_stepFunctions, new GraphLoadStateMachineOptions("arn:graph-load"), NullLogger<StepFunctionsGraphLoadStarter>.Instance);

    [Fact]
    public async Task StartAsync_StartsTheGraphLoad_PinnedToTheScan()
    {
        var (tenantId, scanId) = (Guid.NewGuid(), Guid.NewGuid());
        StartExecutionRequest? sent = null;
        _stepFunctions.StartExecutionAsync(Arg.Do<StartExecutionRequest>(r => sent = r), Arg.Any<CancellationToken>())
            .Returns(new StartExecutionResponse());

        await CreateStarter().StartAsync(tenantId, scanId);

        Assert.NotNull(sent);
        Assert.Equal("arn:graph-load", sent.StateMachineArn);
        Assert.Equal($"graph-load-{scanId}", sent.Name);
        using var input = JsonDocument.Parse(sent.Input);
        Assert.Equal(tenantId, input.RootElement.GetProperty("TenantId").GetGuid());
        Assert.Equal(scanId, input.RootElement.GetProperty("ScanId").GetGuid());
    }

    /// <summary>Every evaluation after the first lands here.</summary>
    [Fact]
    public async Task StartAsync_AlreadyStarted_IsNotAnError()
    {
        _stepFunctions.StartExecutionAsync(Arg.Any<StartExecutionRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new ExecutionAlreadyExistsException("exists"));

        await CreateStarter().StartAsync(Guid.NewGuid(), Guid.NewGuid());
    }
}
