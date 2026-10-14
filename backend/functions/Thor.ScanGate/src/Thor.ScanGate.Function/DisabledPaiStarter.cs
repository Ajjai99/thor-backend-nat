using Microsoft.Extensions.Logging;
using Thor.ScanGate.Core;

namespace Thor.ScanGate.Function;

/// <summary>
/// Where the environment does not run PAI (<c>THOR_PAI_STATE_MACHINE_ARN</c> empty): a scan that
/// reaches the point PAI would start is only logged. The gate still exists there, because it is what
/// starts every scan's graph load.
/// </summary>
public sealed class DisabledPaiStarter(ILogger<DisabledPaiStarter> logger) : IPaiStarter
{
    public Task StartAsync(Guid tenantId, Guid scanId, CancellationToken cancellationToken = default)
    {
        logger.LogInformation("Scan {ScanId} is complete; PAI is not deployed in this environment", scanId);
        return Task.CompletedTask;
    }
}
