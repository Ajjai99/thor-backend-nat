using System.Net.WebSockets;
using Gremlin.Net.Driver;
using Gremlin.Net.Driver.Exceptions;
using Gremlin.Net.Driver.Remote;
using Gremlin.Net.Process.Traversal;
using Gremlin.Net.Structure;
using static Gremlin.Net.Process.Traversal.AnonymousTraversalSource;

namespace Thor.Graph;

/// <summary>
/// Writes vertices and edges as chained <c>mergeV</c>/<c>mergeE</c> upserts, several per request,
/// several requests in parallel. Bytecode, not script strings — Neptune does not support script
/// bindings (see <see cref="GraphRelationshipReader"/>) — over GraphSON 3, the only text format whose
/// maps can be keyed by <c>T.id</c> and <c>Direction</c> (see <see cref="GremlinConnectionFactory"/>).
///
/// <para>Every element is matched by its tenant-namespaced id (<see cref="GraphIds"/>), the form
/// Neptune optimizes for concurrent upserts, so a retried request finds what an earlier attempt
/// wrote rather than duplicating it. Vertex properties are updated with <c>single</c> cardinality on a
/// match; on create they take Neptune's default, which holds one value either way.</para>
///
/// <para>Retries only what a later attempt can succeed at: a <c>ConcurrentModificationException</c>
/// (two requests touching the same vertex — inside one scan, never across tenants, since ids are
/// tenant-prefixed), throttling, a writer failover, or a dropped connection. Anything else — an edge
/// naming a vertex that does not exist, say — fails the call.</para>
/// </summary>
public sealed class GraphBatchWriter : IGraphBatchWriter, IDisposable
{
    private const string TenantIdKey = "tenantId";
    private const string PgIdKey = "pgId";

    /// <summary>The Neptune error codes that name a transient condition. They arrive in the response message.</summary>
    private static readonly string[] RetryableNeptuneCodes =
    [
        "ConcurrentModificationException",
        "ThrottlingException",
        "ReadOnlyViolationException",
        "MemoryLimitExceededException",
        "TimeLimitExceededException",
    ];

    private readonly Lazy<GraphTraversalSource> _g;
    private readonly IDisposable? _ownedConnection;
    private readonly GraphBatchWriterOptions _options;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    public GraphBatchWriter(NeptuneOptions neptuneOptions, GraphBatchWriterOptions options)
    {
        // One connection per parallel request: the scheduler below never has more than Parallelism in
        // flight, and the second slot per connection only absorbs a reconnect racing a new request.
        var factory = new GremlinConnectionFactory(
            neptuneOptions,
            new ConnectionPoolSettings { PoolSize = options.Parallelism, MaxInProcessPerConnection = 2 });
        _g = new Lazy<GraphTraversalSource>(() => Traversal().With(new DriverRemoteConnection(factory.Client, "g")));
        _ownedConnection = factory;
        _options = options;
        _delay = Task.Delay;
    }

    /// <summary>Test-only seam — a fake or local client, and a delay that need not actually wait.</summary>
    internal GraphBatchWriter(IGremlinClient client, GraphBatchWriterOptions options, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _g = new Lazy<GraphTraversalSource>(() => Traversal().With(new DriverRemoteConnection(client, "g")));
        _ownedConnection = null;
        _options = options;
        _delay = delay ?? Task.Delay;
    }

    public Task<GraphWriteStats> UpsertVerticesAsync(
        Guid tenantId, IReadOnlyList<GraphVertexUpsert> vertices, CancellationToken cancellationToken = default) =>
        WriteAsync(
            Batch(vertices, v => 3 + CountWritable(v.Properties)),
            batch => VertexTraversal(tenantId, batch),
            vertices.Count,
            cancellationToken);

    public Task<GraphWriteStats> UpsertEdgesAsync(
        Guid tenantId, IReadOnlyList<GraphEdgeUpsert> edges, CancellationToken cancellationToken = default) =>
        WriteAsync(
            Batch(edges, e => 2 + CountWritable(e.Properties)),
            batch => EdgeTraversal(tenantId, batch),
            edges.Count,
            cancellationToken);

    /// <summary>
    /// Splits <paramref name="items"/> into requests of at most <see cref="GraphBatchWriterOptions.RecordsPerRequest"/>
    /// records and <see cref="GraphBatchWriterOptions.MaxElementsPerRequest"/> elements. An element larger
    /// than the record budget on its own still goes, alone — it cannot be split.
    /// </summary>
    internal IReadOnlyList<IReadOnlyList<T>> Batch<T>(IReadOnlyList<T> items, Func<T, int> records)
    {
        var batches = new List<IReadOnlyList<T>>();
        var current = new List<T>();
        var currentRecords = 0;

        foreach (var item in items)
        {
            var itemRecords = records(item);
            if (current.Count > 0
                && (currentRecords + itemRecords > _options.RecordsPerRequest || current.Count >= _options.MaxElementsPerRequest))
            {
                batches.Add(current);
                current = [];
                currentRecords = 0;
            }

            current.Add(item);
            currentRecords += itemRecords;
        }

        if (current.Count > 0)
        {
            batches.Add(current);
        }

        return batches;
    }

    private async Task<GraphWriteStats> WriteAsync<T, TEnd>(
        IReadOnlyList<IReadOnlyList<T>> batches, Func<IReadOnlyList<T>, GraphTraversal<TEnd, TEnd>> traversal,
        int elementCount, CancellationToken cancellationToken)
    {
        var retries = 0;

        await Parallel.ForEachAsync(
            batches,
            new ParallelOptions { MaxDegreeOfParallelism = _options.Parallelism, CancellationToken = cancellationToken },
            async (batch, ct) =>
            {
                for (var attempt = 1; ; attempt++)
                {
                    try
                    {
                        // id() so the server returns an id rather than serializing the element back.
                        await traversal(batch).Id().Promise(t => t.ToList(), ct);
                        return;
                    }
                    catch (Exception ex) when (attempt < _options.MaxAttempts && IsRetryable(ex))
                    {
                        Interlocked.Increment(ref retries);
                        await _delay(Backoff(attempt), ct);
                    }
                }
            });

        return new GraphWriteStats(elementCount, batches.Count, retries);
    }

    private GraphTraversal<Vertex, Vertex> VertexTraversal(Guid tenantId, IReadOnlyList<GraphVertexUpsert> batch)
    {
        GraphTraversal<Vertex, Vertex>? traversal = null;

        foreach (var vertex in batch)
        {
            var match = new Dictionary<object, object> { [T.Id] = GraphIds.VertexId(tenantId, vertex.EntityType, vertex.Id) };
            var onCreate = new Dictionary<object, object>
            {
                [T.Label] = vertex.EntityType,
                [TenantIdKey] = tenantId.ToString(),
                [PgIdKey] = vertex.Id.ToString(),
            };
            var onMatch = new Dictionary<object, object>();
            foreach (var (key, value) in Writable(vertex.Properties))
            {
                onCreate[key] = value;
                onMatch[key] = value;
            }

            traversal = (traversal is null ? _g.Value.MergeV(match) : traversal.MergeV(match))
                .Option(Merge.OnCreate, onCreate);
            if (onMatch.Count > 0)
            {
                traversal = traversal.Option(Merge.OnMatch, onMatch, Cardinality.Single);
            }
        }

        return traversal!;
    }

    private GraphTraversal<Edge, Edge> EdgeTraversal(Guid tenantId, IReadOnlyList<GraphEdgeUpsert> batch)
    {
        GraphTraversal<Edge, Edge>? traversal = null;

        foreach (var edge in batch)
        {
            var match = new Dictionary<object, object> { [T.Id] = GraphIds.EdgeId(tenantId, edge.EdgeId) };
            var onCreate = new Dictionary<object, object>
            {
                [T.Label] = edge.RelType,
                [Direction.Out] = GraphIds.VertexId(tenantId, edge.From.EntityType, edge.From.Id),
                [Direction.In] = GraphIds.VertexId(tenantId, edge.To.EntityType, edge.To.Id),
                [TenantIdKey] = tenantId.ToString(),
            };
            var onMatch = new Dictionary<object, object>();
            foreach (var (key, value) in Writable(edge.Properties))
            {
                onCreate[key] = value;
                onMatch[key] = value;
            }

            // Edge properties take no cardinality; setting a key again replaces it.
            traversal = (traversal is null ? _g.Value.MergeE(match) : traversal.MergeE(match))
                .Option(Merge.OnCreate, onCreate);
            if (onMatch.Count > 0)
            {
                traversal = traversal.Option(Merge.OnMatch, onMatch);
            }
        }

        return traversal!;
    }

    private static IEnumerable<KeyValuePair<string, object>> Writable(IReadOnlyDictionary<string, object?> properties)
    {
        foreach (var (key, value) in properties)
        {
            if (value is not null)
            {
                yield return new KeyValuePair<string, object>(key, value);
            }
        }
    }

    private static int CountWritable(IReadOnlyDictionary<string, object?> properties) =>
        properties.Count(p => p.Value is not null);

    internal static bool IsRetryable(Exception ex) => ex switch
    {
        ResponseException response => RetryableNeptuneCodes.Any(code => response.Message.Contains(code, StringComparison.Ordinal)),
        ConnectionClosedException or NoConnectionAvailableException or ServerUnavailableException or ConnectionPoolBusyException => true,
        WebSocketException => true,
        _ => false,
    };

    /// <summary>Exponential with full jitter, so requests that collided do not collide again in lockstep.</summary>
    private TimeSpan Backoff(int attempt)
    {
        var ceiling = Math.Min(_options.MaxBackoff.TotalMilliseconds, _options.BaseBackoff.TotalMilliseconds * Math.Pow(2, attempt - 1));
        return TimeSpan.FromMilliseconds(Random.Shared.NextDouble() * ceiling);
    }

    public void Dispose() => _ownedConnection?.Dispose();
}
