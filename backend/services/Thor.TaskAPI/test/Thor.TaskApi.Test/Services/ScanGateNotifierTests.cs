using System.Text.Json;
using Amazon.SQS;
using Amazon.SQS.Model;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Thor.TaskApi.Services;

namespace Thor.TaskApi.Test.Services;

public class ScanGateNotifierTests
{
    private readonly IAmazonSQS _sqs = Substitute.For<IAmazonSQS>();

    private SqsScanGateNotifier CreateNotifier() =>
        new(_sqs, new ScanGateQueueOptions("https://sqs.test/queue"), NullLogger<SqsScanGateNotifier>.Instance);

    /// <summary>The direct shape the gate's GateMessageParser reads: a TenantId and a ScanId, nothing more.</summary>
    [Fact]
    public async Task NotifyTaskFinishedAsync_SendsTheTenantAndScanToTheGateQueue()
    {
        var (tenantId, scanId) = (Guid.NewGuid(), Guid.NewGuid());
        SendMessageRequest? sent = null;
        _sqs.SendMessageAsync(Arg.Do<SendMessageRequest>(r => sent = r), Arg.Any<CancellationToken>()).Returns(new SendMessageResponse());

        await CreateNotifier().NotifyTaskFinishedAsync(tenantId, scanId, CancellationToken.None);

        sent.Should().NotBeNull();
        sent!.QueueUrl.Should().Be("https://sqs.test/queue");
        using var body = JsonDocument.Parse(sent.MessageBody);
        body.RootElement.GetProperty("TenantId").GetGuid().Should().Be(tenantId);
        body.RootElement.GetProperty("ScanId").GetGuid().Should().Be(scanId);
    }

    /// <summary>The task's status is already committed; failing the request would not resend.</summary>
    [Fact]
    public async Task NotifyTaskFinishedAsync_SendFails_DoesNotThrow()
    {
        _sqs.SendMessageAsync(Arg.Any<SendMessageRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new AmazonSQSException("unavailable"));

        var notify = () => CreateNotifier().NotifyTaskFinishedAsync(Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None);

        await notify.Should().NotThrowAsync();
    }
}
