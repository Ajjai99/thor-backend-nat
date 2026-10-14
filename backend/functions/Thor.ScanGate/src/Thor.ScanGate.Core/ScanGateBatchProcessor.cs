using Microsoft.Extensions.Logging;
using Thor.DataConnectionManager.Exceptions;

namespace Thor.ScanGate.Core;

/// <summary>
/// Evaluates each scan a batch of messages concerns once, however many messages name it, and
/// returns the ids of the messages to redeliver: those whose scan failed to evaluate, and those
/// only an expiring upload URL can resolve (<see cref="ScanGateOutcome.RecheckLater"/>).
/// </summary>
public sealed class ScanGateBatchProcessor(
    IScanGateStore store,
    ScanGateEvaluator evaluator,
    ILogger<ScanGateBatchProcessor> logger)
{
    public async Task<IReadOnlyList<string>> ProcessAsync(IReadOnlyList<GateMessage> messages, CancellationToken cancellationToken = default)
    {
        var retry = new List<string>();
        var messagesByScan = new Dictionary<ScanGateRequest, List<string>>();

        foreach (var message in messages)
        {
            try
            {
                var scanId = message.ScanId
                    ?? await store.FindScanIdForManifestAsync(message.TenantId, message.ScanManifestId!.Value, cancellationToken);
                if (scanId is null)
                {
                    logger.LogWarning(
                        "Dropping message {MessageId}: scan manifest {ScanManifestId} does not exist for tenant {TenantId}",
                        message.MessageId, message.ScanManifestId, message.TenantId);
                    continue;
                }

                var request = new ScanGateRequest(message.TenantId, scanId.Value);
                if (!messagesByScan.TryGetValue(request, out var ids))
                {
                    messagesByScan[request] = ids = [];
                }
                ids.Add(message.MessageId);
            }
            catch (TenantNotFoundException ex)
            {
                logger.LogWarning(ex, "Dropping message {MessageId}: tenant {TenantId} has no routing entry", message.MessageId, message.TenantId);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Could not resolve the scan for message {MessageId}; it will be redelivered", message.MessageId);
                retry.Add(message.MessageId);
            }
        }

        foreach (var (request, ids) in messagesByScan)
        {
            try
            {
                var outcome = await evaluator.EvaluateAsync(request, cancellationToken);
                if (outcome == ScanGateOutcome.RecheckLater)
                {
                    retry.AddRange(ids);
                }
            }
            catch (TenantNotFoundException ex)
            {
                logger.LogWarning(ex, "Dropping {Count} message(s) for scan {ScanId}: tenant {TenantId} has no routing entry", ids.Count, request.ScanId, request.TenantId);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Gate evaluation failed for scan {ScanId} (tenant {TenantId}); {Count} message(s) will be redelivered", request.ScanId, request.TenantId, ids.Count);
                retry.AddRange(ids);
            }
        }

        return retry;
    }
}
