using Microsoft.Extensions.Logging;

namespace Thor.Workflows.Pai.Persistence;

/// <summary>
/// Derives PAI's RunId via the shared algorithm with PAI's own namespace GUID. The scan stands where
/// the other modules pass a manifest: PAI runs once per scan, so the same scan always derives the
/// same run and a retried execution collapses onto the same rows.
/// </summary>
internal static class PaiRunIdentity
{
    /// <summary>Fixed namespace GUID for this module's deterministic run ids. Never change once shipped.</summary>
    private static readonly Guid Namespace = Guid.Parse("3e8a1c52-7b4d-4f19-9a6e-2c5d8b0f4a71");

    public static Guid Derive(PaiRequest request, ILogger logger) =>
        Thor.Workflows.Hosting.DeterministicRunId.Derive(request.TenantId, request.ScanId, request.RunId, Namespace, logger);
}
