using Gremlin.Net.Driver.Remote;
using Gremlin.Net.Process.Traversal;
using Thor.Graph.Test.Fixtures;
using static Gremlin.Net.Process.Traversal.AnonymousTraversalSource;

namespace Thor.Graph.Test;

/// <summary>
/// Runs the stores against a real Gremlin Server (Docker required). TinkerGraph is not Neptune, so this
/// proves the traversals are right; <see cref="GraphStoreTests"/> proves they are sent as bytecode.
/// </summary>
public sealed class GraphStoreIntegrationTests(GremlinServerFixture fixture) : IClassFixture<GremlinServerFixture>
{
    private static readonly IReadOnlyDictionary<string, object?> NoProps = new Dictionary<string, object?>();

    private readonly Guid _tenantId = Guid.NewGuid();

    private GraphVertexStore Vertices => new(fixture.Client);

    private GraphEdgeStore Edges => new(fixture.Client);

    private GraphTraversalSource G => Traversal().With(new DriverRemoteConnection(fixture.Client, "g"));

    private string Vid(GraphVertexRef vertex) => GraphIds.VertexId(_tenantId, vertex.EntityType, vertex.Id);

    private async Task<(GraphVertexRef Account, GraphVertexRef Group)> SeedPairAsync()
    {
        var account = new GraphVertexRef("account", Guid.NewGuid());
        var group = new GraphVertexRef("grp", Guid.NewGuid());
        await Vertices.UpsertVertexAsync(_tenantId, account.EntityType, account.Id, NoProps);
        await Vertices.UpsertVertexAsync(_tenantId, group.EntityType, group.Id, NoProps);
        return (account, group);
    }

    private async Task<long> EdgeCountAsync(GraphVertexRef from, string relType) =>
        (await G.V(Vid(from)).OutE(relType).Count().Promise(t => t.Next()));

    [Fact]
    public async Task UpsertVertexAsync_CreatesTheVertexOnceAndUpdatesItsProperties()
    {
        var account = new GraphVertexRef("account", Guid.NewGuid());

        await Vertices.UpsertVertexAsync(_tenantId, "account", account.Id, new Dictionary<string, object?> { ["displayName"] = "Alice" });
        await Vertices.UpsertVertexAsync(_tenantId, "account", account.Id, new Dictionary<string, object?> { ["displayName"] = "Alice B" });

        Assert.Equal(1, await G.V(Vid(account)).Count().Promise(t => t.Next()));
        Assert.Equal("Alice B", await G.V(Vid(account)).Values<string>("displayName").Promise(t => t.Next()));
        Assert.Equal(_tenantId.ToString(), await G.V(Vid(account)).Values<string>("tenantId").Promise(t => t.Next()));
        Assert.Equal(account.Id.ToString(), await G.V(Vid(account)).Values<string>("pgId").Promise(t => t.Next()));
    }

    [Fact]
    public async Task DeleteVertexAsync_RemovesOnlyThatVertex()
    {
        var (account, group) = await SeedPairAsync();

        await Vertices.DeleteVertexAsync(_tenantId, account.EntityType, account.Id);

        Assert.Equal(0, await G.V(Vid(account)).Count().Promise(t => t.Next()));
        Assert.Equal(1, await G.V(Vid(group)).Count().Promise(t => t.Next()));
    }

    [Fact]
    public async Task DeleteVertexAsync_VertexNeverExisted_DoesNotThrow()
    {
        await Vertices.DeleteVertexAsync(_tenantId, "account", Guid.NewGuid());
    }

    [Fact]
    public async Task UpsertEdgeAsync_CreatesOneEdgeAndUpdatesItsProperties()
    {
        var (account, group) = await SeedPairAsync();

        await Edges.UpsertEdgeAsync(_tenantId, "HAS_ACCESS", account, group, new Dictionary<string, object?> { ["is_admin"] = false });
        await Edges.UpsertEdgeAsync(_tenantId, "HAS_ACCESS", account, group, new Dictionary<string, object?> { ["is_admin"] = true });

        Assert.Equal(1, await EdgeCountAsync(account, "HAS_ACCESS"));
        Assert.Equal(true, await G.V(Vid(account)).OutE("HAS_ACCESS").Values<object>("is_admin").Promise(t => t.Next()));
        Assert.Equal(_tenantId.ToString(), await G.V(Vid(account)).OutE("HAS_ACCESS").Values<string>("tenantId").Promise(t => t.Next()));
    }

    [Fact]
    public async Task DeleteEdgeAsync_RemovesOnlyTheNamedEdge_AndLeavesTheVertices()
    {
        var (account, group) = await SeedPairAsync();
        await Edges.UpsertEdgeAsync(_tenantId, "MEMBER_OF", account, group, NoProps);
        await Edges.UpsertEdgeAsync(_tenantId, "HAS_ACCESS", account, group, NoProps);

        await Edges.DeleteEdgeAsync(_tenantId, "MEMBER_OF", account, group);

        Assert.Equal(0, await EdgeCountAsync(account, "MEMBER_OF"));
        Assert.Equal(1, await EdgeCountAsync(account, "HAS_ACCESS"));
        Assert.Equal(1, await G.V(Vid(account)).Count().Promise(t => t.Next()));
        Assert.Equal(1, await G.V(Vid(group)).Count().Promise(t => t.Next()));
    }

    [Fact]
    public async Task DeleteEdgeAsync_EdgeNeverExisted_DoesNotThrow()
    {
        var (account, group) = await SeedPairAsync();

        await Edges.DeleteEdgeAsync(_tenantId, "MEMBER_OF", account, group);
    }
}
