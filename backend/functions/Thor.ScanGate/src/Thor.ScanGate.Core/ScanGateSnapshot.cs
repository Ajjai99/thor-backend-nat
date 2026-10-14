using Thor.DataLayer.Models.Tenants;

namespace Thor.ScanGate.Core;

/// <summary>
/// Everything the gate reads about one scan, loaded together. <see cref="Reconciles"/> and
/// <see cref="GraphLoads"/> are the scan's own reconcile and graph-load rows (normally zero or one
/// each), which carry the scan rather than a manifest and so are not among <see cref="ManifestWorkflows"/>.
/// </summary>
public sealed record ScanGateSnapshot(
    IReadOnlyList<ScanTask> Tasks,
    IReadOnlyDictionary<string, int> FileCountsByStatus,
    IReadOnlyList<ScanFile> PendingFiles,
    IReadOnlyList<Guid> ManifestIds,
    IReadOnlyList<WorkflowEntity> ManifestWorkflows,
    IReadOnlyList<WorkflowEntity> Reconciles,
    IReadOnlyList<WorkflowEntity> GraphLoads);
