using System.Text.Json;
using Amazon.SQS;
using Amazon.SQS.Model;

namespace Thor.TaskApi.Services;

/// <summary>
/// Tells the scan-completion gate that a task of a scan reached a terminal status, so the gate looks
/// at the scan again. Without it the gate re-evaluates only when a workflow execution ends, and a task
/// that reports done after its scan's last manifest finished would leave the scan waiting forever —
/// its graph never written, PAI never started.
/// </summary>
public interface IScanGateNotifier
{
    Task NotifyTaskFinishedAsync(Guid tenantId, Guid scanId, CancellationToken cancellationToken);
}

/// <summary>The gate's ingress queue (infra/src/modules/scan_gate).</summary>
public sealed record ScanGateQueueOptions(string QueueUrl);

/// <summary>
/// Sends <c>{ TenantId, ScanId }</c>, the direct message shape the gate's parser reads. The gate
/// evaluates a scan idempotently, so a duplicate is harmless.
///
/// <para>A failure to send is logged, not thrown: the task's status is already committed, and failing
/// the request would not resend — a retried status report finds the task no longer in progress. The
/// scan then waits for its next workflow event; if there is none, the error here is the trail.</para>
/// </summary>
public sealed class SqsScanGateNotifier(IAmazonSQS sqs, ScanGateQueueOptions options, ILogger<SqsScanGateNotifier> logger) : IScanGateNotifier
{
    public async Task NotifyTaskFinishedAsync(Guid tenantId, Guid scanId, CancellationToken cancellationToken)
    {
        try
        {
            await sqs.SendMessageAsync(new SendMessageRequest
            {
                QueueUrl = options.QueueUrl,
                MessageBody = JsonSerializer.Serialize(new { TenantId = tenantId, ScanId = scanId }),
            }, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Could not notify the scan-completion gate for scan {ScanId} (tenant {TenantId})", scanId, tenantId);
        }
    }
}

/// <summary>Where no gate is deployed — local development, or an environment without ingestion.</summary>
public sealed class DisabledScanGateNotifier : IScanGateNotifier
{
    public Task NotifyTaskFinishedAsync(Guid tenantId, Guid scanId, CancellationToken cancellationToken) => Task.CompletedTask;
}
