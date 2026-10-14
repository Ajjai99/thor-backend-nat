namespace Thor.Workflows.GraphLoad;

/// <summary>
/// Where a scan's graph load keeps its plan and id files in the graph-load bucket. Everything for one
/// scan sits under <see cref="ScanPrefix"/>, and that prefix starts with the tenant, so a key can be
/// checked against the tenant a request claims before anything is read from it.
///
/// <para>The keys are fixed per scan: a re-run of start-run rewrites every file it names and writes the
/// plan last, so the plan never names a file from another attempt. Anything an earlier, larger attempt
/// left beyond that is not in the plan, and the bucket's lifecycle rule expires it (the step's role
/// cannot delete).</para>
/// </summary>
internal static class GraphLoadKeys
{
    public static string ScanPrefix(Guid tenantId, Guid scanId) => $"graph-load/{tenantId:N}/{scanId:N}/";

    public static string Plan(Guid tenantId, Guid scanId) => $"{ScanPrefix(tenantId, scanId)}plan.json";

    public static string Chunk(Guid tenantId, Guid scanId, string kind, int index) =>
        $"{ScanPrefix(tenantId, scanId)}ids/{kind}-{index:D5}.json";

    /// <summary>Fails closed on a key outside this tenant's and scan's prefix — the request's tenant is what vertex ids are minted from.</summary>
    public static void EnsureBelongsTo(string key, Guid tenantId, Guid scanId)
    {
        if (!key.StartsWith(ScanPrefix(tenantId, scanId), StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Graph load key '{key}' is not under tenant {tenantId} scan {scanId}.");
        }
    }
}
