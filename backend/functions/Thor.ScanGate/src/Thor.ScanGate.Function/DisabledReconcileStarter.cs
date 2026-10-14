using Thor.ScanGate.Core;

namespace Thor.ScanGate.Function;

/// <summary>
/// Where the environment does not run reconcile (<c>THOR_RECONCILE_STATE_MACHINE_ARN</c> empty): no
/// run is started, so the gate goes straight on to the graph load and nothing absent is deleted.
/// </summary>
public sealed class DisabledReconcileStarter : IReconcileStarter
{
    public Task<bool> StartAsync(Guid tenantId, Guid scanId, CancellationToken cancellationToken = default) =>
        Task.FromResult(false);
}
