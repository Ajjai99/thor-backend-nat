using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Thor.DataLayer.Models.Tenants;
using Thor.Graph;
using Thor.Workflows.Pai.Models;
using Thor.Workflows.Pai.Privilege;
using Thor.Workflows.Pai.Steps;

namespace Thor.Workflows.Pai.Tests.Fixtures;

/// <summary>
/// A tenant as PAI sees it, kept consistent across both stores the way ingestion keeps them: the graph
/// (<see cref="InMemoryGraph"/>), the Postgres rows PAI reads (accounts, edges), and the change events
/// each change leaves behind until <see cref="CompleteScanAsync"/> writes them out as one scan.
///
/// <para>Runs PAI the way the state machine does — start, expand until done, fan-out waves with
/// tolerated chunks, finalize — and computes what the result should be by brute force
/// (<see cref="ExpectedRoutesAsync"/>), independently of how PAI gets there.</para>
/// </summary>
internal sealed class PaiWorld(TenantDatabaseFixture db)
{
    public const string MemberOf = "MEMBER_OF";
    public const string HasAccess = "HAS_ACCESS";

    /// <summary>Small, so even a modest graph spans several chunks and waves.</summary>
    private const int PathsPerChunk = 3;
    private const int ChunksPerWave = 2;

    private readonly List<(string EntityType, Guid Id, string Change)> _pending = [];
    private readonly Dictionary<Guid, string> _labels = [];

    public Guid TenantId { get; } = Guid.NewGuid();

    public InMemoryGraph Graph { get; } = new();

    public async Task<Guid> AddAccountAsync(string name)
    {
        var account = new Account
        {
            Id = Guid.NewGuid(), SourceId = db.SourceId, ConnectorType = 308, NativeId = name, AccountKind = "user", IsHuman = true,
            DisplayName = name, SamAccountName = name, Upn = $"{name}@corp.local", Email = "", DomainName = "corp.local",
            FilerName = "", NativeAccountId = name, AccountTypeId = WellKnownAccountTypes.Unclassified, RawAttributes = "{}",
            ContentHash = "h", HashVersion = 1, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        };
        await using (var context = db.NewContext())
        {
            context.Accounts.Add(account);
            await context.SaveChangesAsync();
        }

        AddVertex(account.Id, "account", new() { ["displayName"] = name, ["connectorType"] = 308 });
        _pending.Add(("account", account.Id, "inserted"));
        return account.Id;
    }

    public Guid AddGroup(string displayName, int connectorType = 308)
    {
        var id = Guid.NewGuid();
        AddVertex(id, "grp", new() { ["displayName"] = displayName, ["connectorType"] = connectorType });
        _pending.Add(("grp", id, "inserted"));
        return id;
    }

    public Guid AddAsset(string assetType, int connectorType)
    {
        var id = Guid.NewGuid();
        AddVertex(id, "asset", new() { ["assetType"] = assetType, ["displayName"] = assetType, ["connectorType"] = connectorType });
        _pending.Add(("asset", id, "inserted"));
        return id;
    }

    /// <summary>A <paramref name="member"/> → <paramref name="container"/> edge, in both stores, with the event the edge gate would write.</summary>
    public async Task ConnectAsync(Guid member, Guid container, string rel = MemberOf)
    {
        if (!Graph.AddEdge(member, container, rel))
        {
            return;
        }

        var edgeId = await UpsertEdgeRowAsync(member, container, rel, isDeleted: false);
        _pending.Add(("edge", edgeId, "inserted"));
    }

    public async Task DisconnectAsync(Guid member, Guid container, string rel = MemberOf)
    {
        if (!Graph.RemoveEdge(member, container, rel))
        {
            return;
        }

        var edgeId = await UpsertEdgeRowAsync(member, container, rel, isDeleted: true);
        _pending.Add(("edge", edgeId, "deleted"));
    }

    public void Rename(Guid group, string displayName)
    {
        Graph.SetProperty(group, "displayName", displayName);
        _pending.Add(("grp", group, "updated"));
    }

    /// <summary>
    /// The vertex goes with its edges, as graph-load drops it — and deliberately no edge events, the
    /// worst case: only the group's own event says anything changed.
    /// </summary>
    public void DeleteGroup(Guid group)
    {
        Graph.RemoveVertex(group);
        _pending.Add(("grp", group, "deleted"));
    }

    public async Task DeleteAccountAsync(Guid account)
    {
        Graph.RemoveVertex(account);
        await using var context = db.NewContext();
        await context.Accounts.Where(a => a.Id == account).ExecuteUpdateAsync(s => s.SetProperty(a => a.IsDeleted, true));
        _pending.Add(("account", account, "deleted"));
    }

    /// <summary>A scan through the gate: its manifest, the pending change events, and <c>analysis_completed_at</c>.</summary>
    public async Task<Guid> CompleteScanAsync()
    {
        await using var context = db.NewContext();
        var scan = new Scan
        {
            Id = Guid.NewGuid(), ScanConfigId = db.ScanConfigId, ScanType = "delta", Status = "completed",
            AnalysisCompletedAt = DateTimeOffset.UtcNow,
        };
        var manifest = new ScanManifest
        {
            Id = Guid.NewGuid(), ScanId = scan.Id, FileLocations = [], Status = "completed",
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        };
        context.Scans.Add(scan);
        context.ScanManifests.Add(manifest);
        context.IngestChangeEvents.AddRange(_pending.Select(p => new IngestChangeEvent
        {
            Id = Guid.NewGuid(), ScanManifestId = manifest.Id, ScanId = scan.Id, EntityType = p.EntityType, EntityId = p.Id,
            ChangeType = p.Change, OccurredAt = DateTimeOffset.UtcNow,
        }));
        await context.SaveChangesAsync();
        _pending.Clear();
        return scan.Id;
    }

    /// <summary>Starts a run — the first half of <see cref="RunPaiAsync"/>, so a test can interleave another run before finishing it.</summary>
    public async Task<(PaiRequest Request, PaiWave Wave)> StartPaiAsync(Guid scanId)
    {
        var request = new PaiRequest(TenantId, scanId);
        var step = new PaiStartRunStep(
            NullLoggerFactory.Instance, db.ConnectionManager, Graph, () => new PrivilegeDefinitionProvider().GetAll(), PathsPerChunk, ChunksPerWave);
        await using var context = db.NewContext();
        return (request, await step.RunAsync(context, request));
    }

    /// <summary>Everything after start-run, as the state machine sequences it. <paramref name="failChunk"/> fails the chunks it picks, as a crashed Map item would.</summary>
    public async Task FinishPaiAsync(
        PaiRequest request, PaiWave wave, Func<PaiChunkRequest, bool>? failChunk = null, Func<Task>? beforeFinalize = null)
    {
        // A zero budget runs one level per call, so the resume path is exercised on every run.
        var expand = new PaiExpandStep(NullLoggerFactory.Instance, db.ConnectionManager, Graph, TimeSpan.Zero, maxPathsPerTarget: 10_000);
        while (true)
        {
            await using var context = db.NewContext();
            if (!(await expand.RunAsync(context, request)).IsInProgress)
            {
                break;
            }
        }

        var fanout = new PaiFanoutStep(NullLoggerFactory.Instance, db.ConnectionManager, Graph);
        var recordChunkFailure = new PaiRecordChunkFailureStep(NullLoggerFactory.Instance, db.ConnectionManager);
        while (true)
        {
            var results = new List<PaiChunkSummary>();
            foreach (var chunk in wave.Chunks)
            {
                if (failChunk?.Invoke(chunk) == true)
                {
                    await using var failureContext = db.NewContext();
                    await recordChunkFailure.RunAsync(failureContext, new PaiChunkFailureRequest(
                        chunk.TenantId, chunk.ScanId, chunk.RunId, chunk.Offset, chunk.Limit, new PaiFailureCause("Injected", "test")));
                    results.Add(new PaiChunkSummary(Guid.Empty, chunk.Offset, 0, 0, Failed: true));
                    continue;
                }

                await using var read = db.NewContext();
                await using var write = db.NewContext();
                results.Add(await fanout.RunAsync(read, write, chunk));
            }

            wave = PaiWavePlanner.NextWaveAfter(request, wave.WaveIndex, results, PathsPerChunk, ChunksPerWave);
            if (!wave.HasMore)
            {
                break;
            }
        }

        if (beforeFinalize is not null)
        {
            await beforeFinalize();
        }

        await using var finalizeContext = db.NewContext();
        await new PaiFinalizeStep(NullLoggerFactory.Instance, db.ConnectionManager).RunAsync(finalizeContext, request);
    }

    public async Task RunPaiAsync(Guid scanId, Func<PaiChunkRequest, bool>? failChunk = null)
    {
        var (request, wave) = await StartPaiAsync(scanId);
        await FinishPaiAsync(request, wave, failChunk);
    }

    /// <summary>What <c>pai_result</c> holds, as "definition|account → … → target" keys.</summary>
    public async Task<HashSet<string>> ActualRoutesAsync()
    {
        await using var context = db.NewContext();
        var rows = await context.PaiResults.AsNoTracking().Select(r => new { r.PrivilegeDefinitionId, r.AccessPath }).ToListAsync();
        return [.. rows.Select(r => Key(r.PrivilegeDefinitionId,
            JsonDocument.Parse(r.AccessPath).RootElement.EnumerateArray().Select(hop => hop.GetProperty("node_id").GetGuid())))];
    }

    /// <summary>
    /// Every route the graph holds right now, by brute force: from each matching target, every simple
    /// path down through groups (transitive definitions only) to an account, within the depth cap.
    /// </summary>
    public async Task<HashSet<string>> ExpectedRoutesAsync()
    {
        await using var context = db.NewContext();
        var definitions = await context.PrivilegeDefinitions.AsNoTracking().Where(d => d.IsActive).ToListAsync();
        var expected = new HashSet<string>();

        foreach (var definition in definitions)
        {
            var pattern = PrivilegePatternJson.Parse(definition.PathPattern, definition.Name, definition.ConnectorType);
            var targets = await Graph.MatchingTargetsAsync(TenantId, pattern, [.. Graph.Vertices.Keys]);
            foreach (var target in targets)
            {
                Walk([target]);
            }

            void Walk(List<Guid> path)
            {
                foreach (var (from, _, _) in Graph.Edges.Where(e => e.To == path[^1] && e.Rel == pattern.EdgeType))
                {
                    var label = Graph.Vertices[from].Label;
                    if (label == "account")
                    {
                        expected.Add(Key(definition.Id, [from, .. Enumerable.Reverse(path)]));
                    }
                    else if (label == "grp" && pattern.IsTransitive && !path.Contains(from) && path.Count <= PaiWaves.DepthCap)
                    {
                        Walk([.. path, from]);
                    }
                }
            }
        }

        return expected;
    }

    public async Task<string?> RunStatusAsync(Guid scanId)
    {
        await using var context = db.NewContext();
        return await context.Workflows.AsNoTracking().Where(w => w.WorkflowType == "pai" && w.ScanId == scanId).Select(w => w.Status).SingleOrDefaultAsync();
    }

    private static string Key(Guid definitionId, IEnumerable<Guid> nodes) => $"{definitionId}|{string.Join(">", nodes)}";

    private void AddVertex(Guid id, string label, Dictionary<string, object> props)
    {
        Graph.AddVertex(id, label, props);
        _labels[id] = label;
    }

    private async Task<Guid> UpsertEdgeRowAsync(Guid from, Guid to, string rel, bool isDeleted)
    {
        await using var context = db.NewContext();
        var row = await context.Edges.SingleOrDefaultAsync(e => e.FromId == from && e.ToId == to && e.RelType == rel);
        if (row is null)
        {
            row = new Edge
            {
                Id = Guid.NewGuid(), FromId = from, FromType = _labels[from], ToId = to, ToType = _labels[to], RelType = rel,
                ContentHash = "h", CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
            };
            context.Edges.Add(row);
        }

        row.IsDeleted = isDeleted;
        await context.SaveChangesAsync();
        return row.Id;
    }
}
