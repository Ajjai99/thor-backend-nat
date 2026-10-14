using System.Net;
using Amazon.S3;
using Thor.DataConnectionManager;
using Thor.DataLayer.Data;
using Thor.Graph;
using Thor.S3;

namespace Thor.Workflows.GraphLoad.Tests;

/// <summary>An S3 bucket in a dictionary: what one step puts, the next can get. A missing key answers as S3 does, 404.</summary>
internal sealed class InMemoryS3ObjectStore : IS3ObjectStore
{
    private readonly object _lock = new();

    public Dictionary<string, byte[]> Objects { get; } = [];

    public Task<IReadOnlyList<string>> ListKeysAsync(string bucket, string prefix, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            return Task.FromResult<IReadOnlyList<string>>([.. Objects.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal))]);
        }
    }

    public Task<byte[]> GetObjectAsync(string bucket, string key, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            return Objects.TryGetValue(key, out var content)
                ? Task.FromResult(content)
                : Task.FromException<byte[]>(new AmazonS3Exception("The specified key does not exist.") { StatusCode = HttpStatusCode.NotFound });
        }
    }

    public Task<long> GetObjectSizeAsync(string bucket, string key, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task PutObjectAsync(string bucket, string key, byte[] content, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            Objects[key] = content;
        }
        return Task.CompletedTask;
    }
}

/// <summary>Records what would have been written to Neptune, in call order.</summary>
internal sealed class RecordingGraphBatchWriter : IGraphBatchWriter
{
    public List<(Guid TenantId, IReadOnlyList<GraphVertexUpsert> Vertices)> VertexCalls { get; } = [];

    public List<(Guid TenantId, IReadOnlyList<GraphEdgeUpsert> Edges)> EdgeCalls { get; } = [];

    public Task<GraphWriteStats> UpsertVerticesAsync(Guid tenantId, IReadOnlyList<GraphVertexUpsert> vertices, CancellationToken cancellationToken = default)
    {
        VertexCalls.Add((tenantId, vertices));
        return Task.FromResult(new GraphWriteStats(vertices.Count, 1, 0));
    }

    public Task<GraphWriteStats> UpsertEdgesAsync(Guid tenantId, IReadOnlyList<GraphEdgeUpsert> edges, CancellationToken cancellationToken = default)
    {
        EdgeCalls.Add((tenantId, edges));
        return Task.FromResult(new GraphWriteStats(edges.Count, 1, 0));
    }
}

internal sealed class RecordingGraphVertexStore : IGraphVertexStore
{
    private readonly object _lock = new();

    public List<(Guid TenantId, string EntityType, Guid Id)> Deleted { get; } = [];

    public Task UpsertVertexAsync(Guid tenantId, string entityType, Guid id, IReadOnlyDictionary<string, object?> properties, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task UpsertVerticesAsync(Guid tenantId, string entityType, IReadOnlyList<(Guid Id, IReadOnlyDictionary<string, object?> Properties)> vertices, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task DeleteVertexAsync(Guid tenantId, string entityType, Guid id, CancellationToken cancellationToken = default)
    {
        lock (_lock) { Deleted.Add((tenantId, entityType, id)); }
        return Task.CompletedTask;
    }
}

internal sealed class RecordingGraphEdgeStore : IGraphEdgeStore
{
    private readonly object _lock = new();

    public List<(Guid TenantId, string RelType, GraphVertexRef From, GraphVertexRef To)> Deleted { get; } = [];

    public Task UpsertEdgeAsync(Guid tenantId, string relType, GraphVertexRef from, GraphVertexRef to, IReadOnlyDictionary<string, object?> properties, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task DeleteEdgeAsync(Guid tenantId, string relType, GraphVertexRef from, GraphVertexRef to, CancellationToken cancellationToken = default)
    {
        lock (_lock) { Deleted.Add((tenantId, relType, from, to)); }
        return Task.CompletedTask;
    }
}

internal sealed class FakeTenantConnectionManager(Func<Guid, TenantDbContext> factory) : ITenantConnectionManager
{
    public Task<Npgsql.NpgsqlConnection> GetValidatedConnectionAsync(Guid tenantId, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<TenantDbContext> GetTenantDbContextAsync(Guid tenantId, CancellationToken cancellationToken = default) =>
        Task.FromResult(factory(tenantId));
}
