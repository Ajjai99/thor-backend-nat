namespace Thor.ScanGate.Core;

/// <summary>Starts a scan's graph-load workflow. Must be idempotent per scan.</summary>
public interface IGraphLoadStarter
{
    Task StartAsync(Guid tenantId, Guid scanId, CancellationToken cancellationToken = default);
}
