using System.Text.Json.Serialization;
using Amazon.Lambda.Core;
using Microsoft.Extensions.DependencyInjection;
using Thor.ScanGate.Core;

[assembly: LambdaSerializer(typeof(Amazon.Lambda.Serialization.SystemTextJson.DefaultLambdaJsonSerializer))]
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("Thor.ScanGate.Test")]

namespace Thor.ScanGate.Function;

/// <summary>The fields this Lambda needs from an SQS event source mapping's event.</summary>
public sealed record SqsEvent([property: JsonPropertyName("Records")] List<SqsMessage> Records);

public sealed record SqsMessage(
    [property: JsonPropertyName("messageId")] string MessageId,
    [property: JsonPropertyName("body")] string Body);

/// <summary>
/// The partial-batch response the event source mapping reads (<c>ReportBatchItemFailures</c>):
/// listed messages return to the queue after its visibility timeout, the rest are deleted.
/// </summary>
public sealed record SqsBatchResponse([property: JsonPropertyName("batchItemFailures")] List<SqsBatchItemFailure> BatchItemFailures);

public sealed record SqsBatchItemFailure([property: JsonPropertyName("itemIdentifier")] string ItemIdentifier);

/// <summary>
/// Lambda entry point for the scan-completion gate queue. Parses each message
/// (<see cref="GateMessageParser"/>) and hands the batch to <see cref="ScanGateBatchProcessor"/>.
///
/// <para>Redelivery is the gate's only timer: a message returned as a batch-item failure comes back
/// after the queue's visibility timeout, which is how a scan waiting on an unexpired upload URL gets
/// looked at again. This Lambda runs in a VPC with no SQS endpoint, so it never calls SQS itself.</para>
/// </summary>
public sealed class Function
{
    private readonly ScanGateBatchProcessor _processor;

    public Function() : this(CompositionRoot.BuildServiceProvider())
    {
    }

    internal Function(IServiceProvider serviceProvider)
    {
        _processor = serviceProvider.GetRequiredService<ScanGateBatchProcessor>();
    }

    public async Task<SqsBatchResponse> FunctionHandler(SqsEvent sqsEvent, ILambdaContext context)
    {
        var messages = new List<GateMessage>();
        foreach (var record in sqsEvent.Records)
        {
            if (GateMessageParser.TryParse(record.MessageId, record.Body) is { } message)
            {
                messages.Add(message);
            }
            else
            {
                context.Logger.LogWarning($"Dropping message {record.MessageId}: not a recognised scan-gate message");
            }
        }

        var retry = await _processor.ProcessAsync(messages);
        return new SqsBatchResponse([.. retry.Select(id => new SqsBatchItemFailure(id))]);
    }
}
