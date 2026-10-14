namespace Thor.Workflows.Ingestion.Tests.EdgeGate;

/// <summary>Re-runs every <see cref="EdgeResolverTests"/> scenario with one owner and one row per chunk, so each crosses a chunk boundary.</summary>
public sealed class EdgeResolverSingleRowBatchTests : EdgeResolverTests
{
    protected override int BatchRows => 1;
}
