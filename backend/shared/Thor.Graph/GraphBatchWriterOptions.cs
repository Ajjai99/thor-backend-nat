namespace Thor.Graph;

/// <summary>
/// The dials of <see cref="GraphBatchWriter"/>. Defaults follow AWS's guidance for Neptune upserts
/// (<c>gremlin-efficient-upserts.html</c>): about 200 records per request, where a record is a label
/// or a property, and well under the ~1,500 Gremlin steps a request may hold.
/// </summary>
public sealed record GraphBatchWriterOptions
{
    /// <summary>Labels plus properties per request. A vertex with a label and 12 properties is 13.</summary>
    public int RecordsPerRequest { get; init; } = 200;

    /// <summary>
    /// Elements per request whatever their size — keeps an all-small batch (an edge with no
    /// properties is two records) from turning into hundreds of merge steps in one request.
    /// </summary>
    public int MaxElementsPerRequest { get; init; } = 100;

    /// <summary>
    /// Requests in flight at once, each on its own connection and each sent only after the previous
    /// one finished — AWS's "one client, one request at a time" shape. Multiplied by however many
    /// writers run concurrently, this is the share of the writer's worker threads one caller takes.
    /// </summary>
    public int Parallelism { get; init; } = 4;

    /// <summary>Attempts per request when Neptune reports a retryable conflict or throttle.</summary>
    public int MaxAttempts { get; init; } = 8;

    public TimeSpan BaseBackoff { get; init; } = TimeSpan.FromMilliseconds(100);

    public TimeSpan MaxBackoff { get; init; } = TimeSpan.FromSeconds(5);
}
