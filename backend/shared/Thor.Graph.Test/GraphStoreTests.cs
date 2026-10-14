using Thor.Graph.Test.Fakes;

namespace Thor.Graph.Test;

/// <summary>
/// Checks what the stores send: bytecode, never a script with bindings — Neptune rejects bindings
/// ("No variable found for ..."). Whether the traversals do the right thing is covered against a real
/// Gremlin Server in <see cref="GraphStoreIntegrationTests"/>.
/// </summary>
public sealed class GraphStoreTests
{
    private readonly Guid _tenantId = Guid.NewGuid();

    private string Vid(string entityType, Guid id) => $"{_tenantId:N}:{entityType}:{id:N}";

    private static void AssertBytecodeWithoutBindings(Gremlin.Net.Driver.Messages.RequestMessage request)
    {
        Assert.Equal("bytecode", request.Operation);
        Assert.False(request.Arguments.ContainsKey("bindings"));
    }

    [Fact]
    public async Task DeleteEdgeAsync_SendsBytecodeDropOfTheNamedEdge()
    {
        var client = new FakeGremlinClient();
        var from = new GraphVertexRef("account", Guid.NewGuid());
        var to = new GraphVertexRef("grp", Guid.NewGuid());

        await new GraphEdgeStore(client).DeleteEdgeAsync(_tenantId, "MEMBER_OF", from, to);

        var request = Assert.Single(client.Requests);
        AssertBytecodeWithoutBindings(request);
        Assert.Equal($"V({Vid("account", from.Id)}).outE(MEMBER_OF).where(inV().hasId({Vid("grp", to.Id)})).drop()", request.Text());
    }

    [Fact]
    public async Task UpsertEdgeAsync_SendsBytecodeCoalesceOfExistingOrNewEdge_WithTenantAndProperties()
    {
        var client = new FakeGremlinClient();
        var from = new GraphVertexRef("account", Guid.NewGuid());
        var to = new GraphVertexRef("grp", Guid.NewGuid());

        await new GraphEdgeStore(client).UpsertEdgeAsync(_tenantId, "HAS_ACCESS", from, to,
            new Dictionary<string, object?> { ["is_admin"] = true, ["note"] = null });

        var request = Assert.Single(client.Requests);
        AssertBytecodeWithoutBindings(request);
        var text = request.Text();
        Assert.StartsWith($"V({Vid("account", from.Id)}).as(a).V({Vid("grp", to.Id)}).as(b).coalesce(", text);
        Assert.Contains("inE(HAS_ACCESS)", text);
        Assert.Contains("addE(HAS_ACCESS).from(a).to(b)", text);
        Assert.Contains($"property(tenantId,{_tenantId})", text);
        Assert.Contains("property(is_admin,True)", text);
        Assert.Contains("property(note,)", text); // a null value is stored as an empty string, as before
    }

    [Fact]
    public async Task DeleteVertexAsync_SendsBytecodeDropOfTheTenantNamespacedVertex()
    {
        var client = new FakeGremlinClient();
        var id = Guid.NewGuid();

        await new GraphVertexStore(client).DeleteVertexAsync(_tenantId, "account", id);

        var request = Assert.Single(client.Requests);
        AssertBytecodeWithoutBindings(request);
        Assert.Equal($"V({Vid("account", id)}).drop()", request.Text());
    }

    [Fact]
    public async Task UpsertVertexAsync_SendsBytecodeUpsert_WithTenantIdAndProperties()
    {
        var client = new FakeGremlinClient();
        var id = Guid.NewGuid();

        await new GraphVertexStore(client).UpsertVertexAsync(_tenantId, "account", id,
            new Dictionary<string, object?> { ["displayName"] = "Alice", ["isDisabled"] = false });

        var request = Assert.Single(client.Requests);
        AssertBytecodeWithoutBindings(request);
        var text = request.Text();
        Assert.StartsWith($"V({Vid("account", id)}).fold().coalesce(unfold(),addV(account)", text);
        Assert.Contains($"tenantId,{_tenantId})", text);
        Assert.Contains($"pgId,{id})", text);
        Assert.Contains("displayName,Alice)", text);
        Assert.Contains("isDisabled,False)", text);
    }

    [Fact]
    public async Task UpsertVerticesAsync_SendsOneBytecodeRequestPerVertex()
    {
        var client = new FakeGremlinClient();
        var noProps = new Dictionary<string, object?>();

        await new GraphVertexStore(client).UpsertVerticesAsync(_tenantId, "grp", [(Guid.NewGuid(), noProps), (Guid.NewGuid(), noProps)]);

        Assert.Equal(2, client.Requests.Count);
        Assert.All(client.Requests, AssertBytecodeWithoutBindings);
    }
}
