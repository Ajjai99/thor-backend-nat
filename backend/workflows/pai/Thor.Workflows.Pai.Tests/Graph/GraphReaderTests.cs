using System.Collections.Concurrent;
using System.Text;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Gremlin.Net.Driver;
using Gremlin.Net.Driver.Messages;
using Gremlin.Net.Driver.Remote;
using Gremlin.Net.Process.Traversal;
using Gremlin.Net.Structure.IO.GraphSON;
using Thor.Graph;
using Xunit;
using static Gremlin.Net.Process.Traversal.AnonymousTraversalSource;

namespace Thor.Workflows.Pai.Tests.Graph;

/// <summary>
/// One Gremlin server for the class, spoken to in GraphSON 3 as production is. TinkerGraph is told to
/// accept any id, because Thor's vertex ids are the tenant-namespaced strings <c>GraphIds</c> builds.
/// </summary>
public sealed class GremlinServerFixture : IAsyncLifetime
{
    private const string TinkerGraphProperties = """
        gremlin.graph=org.apache.tinkerpop.gremlin.tinkergraph.structure.TinkerGraph
        gremlin.tinkergraph.vertexIdManager=ANY
        gremlin.tinkergraph.edgeIdManager=ANY
        gremlin.tinkergraph.vertexPropertyIdManager=ANY
        """;

    private readonly IContainer _container = new ContainerBuilder()
        .WithImage("tinkerpop/gremlin-server:3.7.3")
        .WithPortBinding(8182, true)
        .WithResourceMapping(Encoding.UTF8.GetBytes(TinkerGraphProperties), "/opt/gremlin-server/conf/tinkergraph-empty.properties")
        .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("Channel started at port 8182"))
        .Build();

    public GremlinClient Client { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        Client = new GremlinClient(
            new GremlinServer(_container.Hostname, _container.GetMappedPublicPort(8182), enableSsl: false),
            new GraphSON3MessageSerializer());
    }

    public async Task DisposeAsync()
    {
        Client.Dispose();
        await _container.DisposeAsync();
    }
}

/// <summary>
/// The Gremlin behind <see cref="GraphReader"/>, against a real server. The graph is written through
/// <see cref="GraphBatchWriter"/>, graph-load's writer, so these read exactly what production writes —
/// property names and types included. Each test uses its own tenant ids, and one plants a second
/// tenant's look-alike data to prove reads never cross it.
/// </summary>
public sealed class GraphReaderTests(GremlinServerFixture server) : IClassFixture<GremlinServerFixture>
{
    private static readonly PrivilegePattern DomainAdmins =
        new("MEMBER_OF", "grp", "displayName", PrivilegeMatchOperator.In, ["Domain Admins", "Enterprise Admins"], ConnectorType: 308);

    private GraphBatchWriter Writer => new(server.Client, new GraphBatchWriterOptions());

    [Fact]
    public async Task MatchingTargets_ScansOnlyTheTenantsVerticesOfTheRightLabelAndConnector()
    {
        var tenant = Guid.NewGuid();
        var other = Guid.NewGuid();
        var match = await AddVertexAsync(tenant, "grp", "Domain Admins", 308);
        await AddVertexAsync(tenant, "grp", "Domain Admins", 402);     // another connector's group
        await AddVertexAsync(tenant, "asset", "Domain Admins", 308);   // wrong label
        await AddVertexAsync(tenant, "grp", "Staff", 308);
        await AddVertexAsync(other, "grp", "Domain Admins", 308);      // another tenant

        var reader = new GraphReader(server.Client);

        Assert.Equal([match], await reader.MatchingTargetsAsync(tenant, DomainAdmins, candidateIds: null));
    }

    [Fact]
    public async Task MatchingTargets_WithCandidates_ReturnsOnlyTheCandidatesThatMatch()
    {
        var tenant = Guid.NewGuid();
        var match = await AddVertexAsync(tenant, "grp", "Enterprise Admins", 308);
        var noMatch = await AddVertexAsync(tenant, "grp", "Staff", 308);
        await AddVertexAsync(tenant, "grp", "Domain Admins", 308); // matches, but was not asked about

        var reader = new GraphReader(server.Client);

        Assert.Equal([match], await reader.MatchingTargetsAsync(tenant, DomainAdmins, [match, noMatch, Guid.NewGuid()]));
    }

    [Theory]
    [InlineData(PrivilegeMatchOperator.Equals, "cyberark_safe", true)]
    [InlineData(PrivilegeMatchOperator.Contains, "ark_sa", true)]
    [InlineData(PrivilegeMatchOperator.StartsWith, "cyber", true)]
    [InlineData(PrivilegeMatchOperator.StartsWith, "safe", false)]
    public async Task MatchingTargets_AppliesEachOperator(PrivilegeMatchOperator op, string value, bool matches)
    {
        var tenant = Guid.NewGuid();
        var safe = Guid.NewGuid();
        await Writer.UpsertVerticesAsync(tenant, [new GraphVertexUpsert("asset", safe, new Dictionary<string, object?>
        {
            ["assetType"] = "cyberark_safe",
            ["connectorType"] = (short)401,
        })]);

        var pattern = new PrivilegePattern("HAS_ACCESS", "asset", "assetType", op, [value], ConnectorType: 401);
        var found = await new GraphReader(server.Client).MatchingTargetsAsync(tenant, pattern, candidateIds: null);

        Assert.Equal(matches, found.Contains(safe));
    }

    [Fact]
    public async Task Neighbours_FollowTheEdgeEachWay_FilteredByNeighbourLabel_AndBatchedPerSeedLabel()
    {
        var tenant = Guid.NewGuid();
        var parent = await AddVertexAsync(tenant, "grp", "Parent", 308);
        var child = await AddVertexAsync(tenant, "grp", "Child", 308);
        var account = await AddVertexAsync(tenant, "account", "alice", 308);
        var safe = await AddVertexAsync(tenant, "asset", "safe", 401);
        await AddEdgeAsync(tenant, (child, "grp"), (parent, "grp"), "MEMBER_OF");
        await AddEdgeAsync(tenant, (account, "account"), (parent, "grp"), "MEMBER_OF");
        await AddEdgeAsync(tenant, (account, "account"), (safe, "asset"), "HAS_ACCESS");

        var reader = new GraphReader(server.Client);

        Assert.Equal([new GraphHop(parent, child)],
            await reader.InNeighboursAsync(tenant, [new GraphNodeRef(parent, "grp")], "MEMBER_OF", "grp"));
        Assert.Equal([new GraphHop(parent, account)],
            await reader.InNeighboursAsync(tenant, [new GraphNodeRef(parent, "grp")], "MEMBER_OF", "account"));
        Assert.Equal([new GraphHop(child, parent)],
            await reader.OutNeighboursAsync(tenant, [new GraphNodeRef(child, "grp")], "MEMBER_OF", "grp"));

        // Mixed labels in one call: each is addressed under its own label.
        var members = await reader.InNeighboursAsync(
            tenant, [new GraphNodeRef(parent, "grp"), new GraphNodeRef(safe, "asset")], "HAS_ACCESS", "account");
        Assert.Equal([new GraphHop(safe, account)], members);
    }

    [Fact]
    public async Task Neighbours_NeverCrossIntoAnotherTenant()
    {
        var tenant = Guid.NewGuid();
        var other = Guid.NewGuid();
        var group = await AddVertexAsync(tenant, "grp", "Domain Admins", 308);
        var intruder = await AddVertexAsync(other, "account", "mallory", 308);

        // An edge that should never exist — the writer cannot make one, so it is planted directly — to
        // prove the tenant filter holds on the far side too.
        var g = Traversal().With(new DriverRemoteConnection(server.Client, "g"));
        await g.V(GraphIds.VertexId(other, "account", intruder)).AddE("MEMBER_OF").To(__.V(GraphIds.VertexId(tenant, "grp", group)))
            .Promise(t => t.Iterate());

        var members = await new GraphReader(server.Client).InNeighboursAsync(tenant, [new GraphNodeRef(group, "grp")], "MEMBER_OF", "account");

        Assert.Empty(members);
    }

    [Fact]
    public async Task Neighbours_SplitALargeSeedIntoBatches()
    {
        var tenant = Guid.NewGuid();
        var parent = await AddVertexAsync(tenant, "grp", "Parent", 308);
        var children = new List<Guid>();
        for (var i = 0; i < GraphReader.SeedBatchSize + 5; i++)
        {
            var child = await AddVertexAsync(tenant, "grp", $"Child{i}", 308);
            await AddEdgeAsync(tenant, (child, "grp"), (parent, "grp"), "MEMBER_OF");
            children.Add(child);
        }

        var hops = await new GraphReader(server.Client).OutNeighboursAsync(
            tenant, [.. children.Select(c => new GraphNodeRef(c, "grp"))], "MEMBER_OF", "grp");

        Assert.Equal(children.Count, hops.Count);
        Assert.All(hops, hop => Assert.Equal(parent, hop.NeighbourId));
    }

    [Fact]
    public async Task EveryRead_IsBytecode_WithoutBindings()
    {
        // Neptune rejects script bindings; gremlin-server accepts them, so the results alone cannot show this.
        var tenant = Guid.NewGuid();
        var group = await AddVertexAsync(tenant, "grp", "Domain Admins", 308);
        var client = new RecordingGremlinClient(server.Client);
        var reader = new GraphReader(client);

        await reader.MatchingTargetsAsync(tenant, DomainAdmins, candidateIds: null);
        await reader.MatchingTargetsAsync(tenant, DomainAdmins, [group]);
        await reader.InNeighboursAsync(tenant, [new GraphNodeRef(group, "grp")], "MEMBER_OF", "grp");
        await reader.OutNeighboursAsync(tenant, [new GraphNodeRef(group, "grp")], "MEMBER_OF", "grp");

        Assert.Equal(4, client.Requests.Count);
        Assert.All(client.Requests, request =>
        {
            Assert.Equal(Tokens.ProcessorTraversal, request.Processor);
            Assert.False(request.Arguments.ContainsKey(Tokens.ArgsBindings));
        });
    }

    private async Task<Guid> AddVertexAsync(Guid tenant, string label, string displayName, short connectorType)
    {
        var id = Guid.NewGuid();
        await Writer.UpsertVerticesAsync(tenant, [new GraphVertexUpsert(label, id, new Dictionary<string, object?>
        {
            ["displayName"] = displayName,
            ["connectorType"] = connectorType,
        })]);
        return id;
    }

    private Task AddEdgeAsync(Guid tenant, (Guid Id, string Label) from, (Guid Id, string Label) to, string rel) =>
        Writer.UpsertEdgesAsync(tenant, [new GraphEdgeUpsert(
            Guid.NewGuid(), rel, new GraphVertexRef(from.Label, from.Id), new GraphVertexRef(to.Label, to.Id), new Dictionary<string, object?>())]);

    private sealed class RecordingGremlinClient(IGremlinClient inner) : IGremlinClient
    {
        public ConcurrentQueue<RequestMessage> Requests { get; } = new();

        public Task<ResultSet<T>> SubmitAsync<T>(RequestMessage requestMessage, CancellationToken cancellationToken = default)
        {
            Requests.Enqueue(requestMessage);
            return inner.SubmitAsync<T>(requestMessage, cancellationToken);
        }

        public void Dispose()
        {
        }
    }
}
