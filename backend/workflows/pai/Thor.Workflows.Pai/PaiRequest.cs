namespace Thor.Workflows.Pai;

/// <summary>
/// One PAI run's identity, deserialized from the raw Lambda invocation payload. <see cref="TenantId"/>
/// is trusted as already verified upstream, matching <c>IngestionRequest</c> — the connection-level
/// tenant validator still runs regardless.
///
/// <para>Scoped by <see cref="ScanId"/>, not a manifest: PAI evaluates privilege over the whole graph,
/// so it runs once per scan behind the scan-completion gate (<c>Thor.ScanGate</c>), which starts it
/// with <c>{TenantId, ScanId}</c>. The run id is derived from the scan unless <see cref="RunId"/> pins
/// one; a caller with no scan must pass a <see cref="RunId"/>.</para>
/// </summary>
public sealed record PaiRequest(Guid TenantId, Guid? ScanId = null, Guid? RunId = null)
{
    /// <summary>Throws for a request no run can be started from — before any row is opened, so there is nothing to close.</summary>
    public void Validate()
    {
        if (ScanId is null && RunId is null)
        {
            throw new InvalidOperationException("A PAI run needs a ScanId or an explicit RunId.");
        }
    }
}
