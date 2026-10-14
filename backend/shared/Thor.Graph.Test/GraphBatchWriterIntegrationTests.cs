using Gremlin.Net.Driver.Exceptions;
using Gremlin.Net.Driver.Remote;
using Gremlin.Net.Process.Traversal;
using Thor.Graph.Test.Fixtures;
using static Gremlin.Net.Process.Traversal.AnonymousTraversalSource;

namespace Thor.Graph.Test;

/// <summary>
/// Runs <see cref="GraphBatchWriter"/> against a real Gremlin Server 3.7 (Docker required) — the
/// TinkerPop line Neptune 1.4 implements. It proves the chained <c>mergeV</c>/<c>mergeE</c> requests are
/// valid and idempotent; TinkerGraph is not Neptune, so it does not prove Neptune's throughput or its
/// conflict behaviour.
/// </summary>
public sealed class GraphBatchWriterIntegrationTests(GremlinServerFixture fixture) : IClassFixture<GremlinServerFixture>
{
    private readonly Guid _tenantId = Guid.NewGuid();

    private GraphBatchWriter Writer => new(fixture.Client, new GraphBatchWriterOptions { Parallelism = 2, MaxElementsPerRequest = 10 });

    private GraphTraversalSource G => Traversal().With(new DriverRemoteConnection(fixture.Client, "g"));

    private string Vid(GraphVertexRef vertex) => GraphIds.VertexId(_tenantId, vertex.EntityType, vertex.Id);

    private static Dictionary<string, object?> Props(params (string Key, object? Value)[] pairs) =>
        pairs.ToDictionary(p => p.Key, p => p.Value);

    [Fact]
    public async Task UpsertVerticesAsync_CreatesOnceThenUpdatesInPlace()
    {
        var account = new GraphVertexRef("account", Guid.NewGuid());

        await Writer.UpsertVerticesAsync(_tenantId, [new GraphVertexUpsert("account", account.Id, Props(("displayName", "Alice"), ("isHuman", true)))]);
        await Writer.UpsertVerticesAsync(_tenantId, [new GraphVertexUpsert("account", account.Id, Props(("displayName", "Alice B"), ("isHuman", null)))]);

        Assert.Equal(1, await G.V(Vid(account)).Count().Promise(t => t.Next()));
        Assert.Equal("account", await G.V(Vid(account)).Label().Promise(t => t.Next()));
        Assert.Equal(["Alice B"], await G.V(Vid(account)).Values<string>("displayName").Promise(t => t.ToList()));
        // A null on the second write leaves the first write's value alone.
        Assert.True(await G.V(Vid(account)).Values<bool>("isHuman").Promise(t => t.Next()));
        Assert.Equal(_tenantId.ToString(), await G.V(Vid(account)).Values<string>("tenantId").Promise(t => t.Next()));
        Assert.Equal(account.Id.ToString(), await G.V(Vid(account)).Values<string>("pgId").Promise(t => t.Next()));
    }

    [Fact]
    public async Task UpsertEdgesAsync_ChainsManyPerRequest_AndRewritingFindsTheSameEdges()
    {
        var group = new GraphVertexRef("grp", Guid.NewGuid());
        var accounts = Enumerable.Range(0, 25).Select(_ => new GraphVertexRef("account", Guid.NewGuid())).ToList();
        await Writer.UpsertVerticesAsync(_tenantId,
            [.. accounts.Append(group).Select(v => new GraphVertexUpsert(v.EntityType, v.Id, Props()))]);

        var edges = accounts
            .Select(a => new GraphEdgeUpsert(Guid.NewGuid(), "MEMBER_OF", a, group, Props(("isDirect", true))))
            .ToList();

        await Writer.UpsertEdgesAsync(_tenantId, edges);
        await Writer.UpsertEdgesAsync(_tenantId, [.. edges.Select(e => e with { Properties = Props(("isDirect", false)) })]);

        Assert.Equal(25, await G.V(Vid(group)).InE("MEMBER_OF").Count().Promise(t => t.Next()));
        Assert.Equal(25, await G.V(Vid(group)).InE("MEMBER_OF").Has("isDirect", false).Count().Promise(t => t.Next()));
        var first = edges[0];
        Assert.Equal(Vid(first.From), await G.E(GraphIds.EdgeId(_tenantId, first.EdgeId)).OutV().Id().Promise(t => t.Next()));
    }

    [Fact]
    public async Task UpsertEdgesAsync_EndpointMissing_Fails()
    {
        var account = new GraphVertexRef("account", Guid.NewGuid());
        await Writer.UpsertVerticesAsync(_tenantId, [new GraphVertexUpsert("account", account.Id, Props())]);
        var missing = new GraphVertexRef("grp", Guid.NewGuid());

        await Assert.ThrowsAsync<ResponseException>(() =>
            Writer.UpsertEdgesAsync(_tenantId, [new GraphEdgeUpsert(Guid.NewGuid(), "MEMBER_OF", account, missing, Props())]));
    }
}
