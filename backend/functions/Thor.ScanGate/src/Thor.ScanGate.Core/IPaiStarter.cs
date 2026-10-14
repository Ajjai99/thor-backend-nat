namespace Thor.ScanGate.Core;

/// <summary>Starts the PAI workflow for a scan. Must be idempotent per scan.</summary>
public interface IPaiStarter
{
    Task StartAsync(Guid tenantId, Guid scanId, CancellationToken cancellationToken = default);
}
