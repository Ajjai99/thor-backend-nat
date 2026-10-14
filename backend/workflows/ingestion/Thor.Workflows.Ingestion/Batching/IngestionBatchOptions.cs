namespace Thor.Workflows.Ingestion.Batching;

/// <summary>
/// Row counts that bound each batched SQL statement in promotion and edge resolution, so a
/// statement's runtime follows the batch size rather than the size of the manifest.
/// </summary>
public sealed record IngestionBatchOptions
{
    public const int DefaultRows = 10_000;

    private const string RowsEnvironmentVariable = "THOR_INGESTION_BATCH_ROWS";

    public static IngestionBatchOptions Default { get; } = new(DefaultRows);

    public IngestionBatchOptions(int rows)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(rows, 1);
        Rows = rows;
    }

    /// <summary>Staged rows, delta refs or edges handled per batch.</summary>
    public int Rows { get; }

    /// <summary>Owners resolved per edge-gate chunk — smaller than <see cref="Rows"/> because one owner fans out to many edges.</summary>
    public int OwnersPerEdgeChunk => Math.Max(1, Rows / 10);

    public static IngestionBatchOptions FromEnvironment() =>
        int.TryParse(Environment.GetEnvironmentVariable(RowsEnvironmentVariable), out var rows) && rows > 0
            ? new IngestionBatchOptions(rows)
            : Default;
}
