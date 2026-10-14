using Gremlin.Net.Driver.Exceptions;
using Gremlin.Net.Driver.Messages;
using Thor.Graph.Test.Fakes;

namespace Thor.Graph.Test;

/// <summary>
/// Checks how <see cref="GraphBatchWriter"/> splits work into requests, what each request says, and
/// which failures it retries. Whether the upserts do the right thing in a real graph is covered in
/// <see cref="GraphBatchWriterIntegrationTests"/>.
/// </summary>
public sealed class GraphBatchWriterTests
{
    private static readonly IReadOnlyDictionary<string, object?> NoProps = new Dictionary<string, object?>();

    private readonly Guid _tenantId = Guid.NewGuid();

    private static Task NoDelay(TimeSpan delay, CancellationToken cancellationToken) => Task.CompletedTask;

    private static GraphBatchWriter Writer(FakeGremlinClient client, GraphBatchWriterOptions? options = null) =>
        new(client, options ?? new GraphBatchWriterOptions { Parallelism = 1 }, NoDelay);

    private string Vid(string entityType, Guid id) => $"{_tenantId:N}:{entityType}:{id:N}";

    private static ResponseException Neptune(string code) =>
        new(ResponseStatusCode.ServerError, new Dictionary<string, object>(), $"{{\"code\":\"{code}\",\"detailedMessage\":\"simulated\"}}");

    [Fact]
    public async Task UpsertVerticesAsync_SplitsByTheRecordBudget()
    {
        var client = new FakeGremlinClient();
        // A vertex with no properties is three records: its label, tenantId and pgId.
        var vertices = Enumerable.Range(0, 5).Select(_ => new GraphVertexUpsert("account", Guid.NewGuid(), NoProps)).ToList();

        var stats = await Writer(client, new GraphBatchWriterOptions { Parallelism = 1, RecordsPerRequest = 6 })
            .UpsertVerticesAsync(_tenantId, vertices);

        Assert.Equal(3, client.Requests.Count);
        Assert.Equal(new GraphWriteStats(5, 3, 0), stats);
    }

    [Fact]
    public async Task UpsertEdgesAsync_CapsElementsPerRequestWhateverTheirSize()
    {
        var client = new FakeGremlinClient();
        var edges = Enumerable.Range(0, 7)
            .Select(_ => new GraphEdgeUpsert(Guid.NewGuid(), "MEMBER_OF",
                new GraphVertexRef("account", Guid.NewGuid()), new GraphVertexRef("grp", Guid.NewGuid()), NoProps))
            .ToList();

        await Writer(client, new GraphBatchWriterOptions { Parallelism = 1, MaxElementsPerRequest = 3 })
            .UpsertEdgesAsync(_tenantId, edges);

        Assert.Equal(3, client.Requests.Count);
    }

    [Fact]
    public async Task UpsertVerticesAsync_SendsChainedMergeV_ById_WithoutBindings()
    {
        var client = new FakeGremlinClient();
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();

        await Writer(client).UpsertVerticesAsync(_tenantId,
        [
            new GraphVertexUpsert("account", first, new Dictionary<string, object?> { ["displayName"] = "Alice", ["email"] = null }),
            new GraphVertexUpsert("grp", second, NoProps),
        ]);

        var request = Assert.Single(client.Requests);
        Assert.Equal("bytecode", request.Operation);
        Assert.False(request.Arguments.ContainsKey("bindings"));

        var steps = request.Bytecode().StepInstructions.Select(i => i.OperatorName).ToList();
        // The vertex with a property gets an onMatch; the one without has nothing to update.
        Assert.Equal(["mergeV", "option", "option", "mergeV", "option", "id"], steps);

        var text = request.Text();
        Assert.Contains(Vid("account", first), text);
        Assert.Contains(Vid("grp", second), text);
        Assert.Contains("Alice", text);
        Assert.Contains(_tenantId.ToString(), text);
        Assert.Contains(first.ToString(), text);
        Assert.DoesNotContain("email", text); // a null value is skipped, not written
    }

    [Fact]
    public async Task UpsertVerticesAsync_UpdatesWithSingleCardinalityOnMatch()
    {
        var client = new FakeGremlinClient();

        await Writer(client).UpsertVerticesAsync(_tenantId,
            [new GraphVertexUpsert("account", Guid.NewGuid(), new Dictionary<string, object?> { ["displayName"] = "Alice" })]);

        var onMatch = client.Requests[0].Bytecode().StepInstructions[2];
        Assert.Equal("option", onMatch.OperatorName);
        Assert.Equal(3, onMatch.Arguments.Length);
        Assert.Equal(Gremlin.Net.Process.Traversal.Cardinality.Single, onMatch.Arguments[2]);
    }

    [Fact]
    public async Task UpsertEdgesAsync_SendsMergeE_ByEdgeId_CreatingWithBothEndpoints()
    {
        var client = new FakeGremlinClient();
        var edgeId = Guid.NewGuid();
        var from = new GraphVertexRef("account", Guid.NewGuid());
        var to = new GraphVertexRef("grp", Guid.NewGuid());

        await Writer(client).UpsertEdgesAsync(_tenantId,
            [new GraphEdgeUpsert(edgeId, "MEMBER_OF", from, to, new Dictionary<string, object?> { ["isDirect"] = true })]);

        var request = Assert.Single(client.Requests);
        var steps = request.Bytecode().StepInstructions.Select(i => i.OperatorName).ToList();
        Assert.Equal(["mergeE", "option", "option", "id"], steps);

        var text = request.Text();
        Assert.Contains($"{_tenantId:N}:edge:{edgeId:N}", text);
        Assert.Contains(Vid("account", from.Id), text);
        Assert.Contains(Vid("grp", to.Id), text);
        Assert.Contains("MEMBER_OF", text);
    }

    [Fact]
    public async Task UpsertEdgesAsync_RetriesAConcurrentModification()
    {
        var client = new FakeGremlinClient().Throws(Neptune("ConcurrentModificationException"));
        var edge = new GraphEdgeUpsert(Guid.NewGuid(), "MEMBER_OF",
            new GraphVertexRef("account", Guid.NewGuid()), new GraphVertexRef("grp", Guid.NewGuid()), NoProps);

        var stats = await Writer(client).UpsertEdgesAsync(_tenantId, [edge]);

        Assert.Equal(2, client.Requests.Count);
        Assert.Equal(1, stats.Retries);
    }

    [Fact]
    public async Task UpsertVerticesAsync_RetriesADroppedConnection()
    {
        var client = new FakeGremlinClient().Throws(new ConnectionClosedException(null, "closed"));

        var stats = await Writer(client).UpsertVerticesAsync(_tenantId, [new GraphVertexUpsert("account", Guid.NewGuid(), NoProps)]);

        Assert.Equal(1, stats.Retries);
    }

    [Fact]
    public async Task UpsertEdgesAsync_DoesNotRetryAnErrorALaterAttemptCannotFix()
    {
        // The shape of a mergeE naming a vertex that does not exist.
        var client = new FakeGremlinClient().Throws(Neptune("InternalFailureException"));
        var edge = new GraphEdgeUpsert(Guid.NewGuid(), "MEMBER_OF",
            new GraphVertexRef("account", Guid.NewGuid()), new GraphVertexRef("grp", Guid.NewGuid()), NoProps);

        await Assert.ThrowsAsync<ResponseException>(() => Writer(client).UpsertEdgesAsync(_tenantId, [edge]));
        Assert.Single(client.Requests);
    }

    [Fact]
    public async Task UpsertVerticesAsync_GivesUpAfterMaxAttempts()
    {
        var client = new FakeGremlinClient()
            .Throws(Neptune("ConcurrentModificationException"))
            .Throws(Neptune("ConcurrentModificationException"))
            .Throws(Neptune("ConcurrentModificationException"));

        await Assert.ThrowsAsync<ResponseException>(() =>
            Writer(client, new GraphBatchWriterOptions { Parallelism = 1, MaxAttempts = 3 })
                .UpsertVerticesAsync(_tenantId, [new GraphVertexUpsert("account", Guid.NewGuid(), NoProps)]));
        Assert.Equal(3, client.Requests.Count);
    }

    [Fact]
    public async Task UpsertVerticesAsync_NothingToWrite_SendsNothing()
    {
        var client = new FakeGremlinClient();

        var stats = await Writer(client).UpsertVerticesAsync(_tenantId, []);

        Assert.Empty(client.Requests);
        Assert.Equal(new GraphWriteStats(0, 0, 0), stats);
    }
}
