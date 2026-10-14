namespace Thor.ScanGate.Core;

/// <summary>Starts the reconcile workflow for a scan. Must be idempotent per scan.</summary>
public interface IReconcileStarter
{
    /// <summary>
    /// Starts reconcile, or does nothing if it was already started. Returns <c>false</c> where the
    /// environment does not run reconcile, so there is no run for the gate to wait on.
    /// </summary>
    Task<bool> StartAsync(Guid tenantId, Guid scanId, CancellationToken cancellationToken = default);
}
