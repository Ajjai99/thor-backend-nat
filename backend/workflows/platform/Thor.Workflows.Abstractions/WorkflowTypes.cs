namespace Thor.Workflows.Abstractions;

/// <summary>Known <see cref="Thor.DataLayer.Models.Tenants.WorkflowEntity.WorkflowType"/> values, one per workflow module.</summary>
public static class WorkflowTypes
{
    public const string Ingestion = "ingestion";

    public const string Atre = "atre";

    public const string Ownership = "ownership";

    /// <summary>
    /// The once-per-scan graph load: everything the scan changed written to Neptune together, started
    /// by the scan-completion gate once the scan's ingestion has finished. Its row carries the scan,
    /// not a manifest.
    /// </summary>
    public const string GraphLoad = "graph-load";

    public const string Pai = "pai";

    /// <summary>
    /// The once-per-scan deletion of entities a scan's full exports no longer contain, started by the
    /// scan-completion gate once ingestion has finished and before the graph load. Its row carries the
    /// scan, not a manifest.
    /// </summary>
    public const string Reconcile = "reconcile";
}
