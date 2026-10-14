using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Thor.DataLayer.Models.Tenants;
using Thor.ScanGate.Core;
using Thor.Workflows.Abstractions;

namespace Thor.ScanGate.Test;

/// <summary>Covers <see cref="ScanGateEvaluator"/>'s decision over a loaded snapshot.</summary>
public class ScanGateEvaluatorTests
{
    private static readonly TimeSpan Grace = TimeSpan.FromMinutes(15);

    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly Guid _scanId = Guid.NewGuid();
    private readonly IScanGateStore _store = Substitute.For<IScanGateStore>();
    private readonly IUploadObjectProbe _probe = Substitute.For<IUploadObjectProbe>();
    private readonly IReconcileStarter _reconcileStarter = Substitute.For<IReconcileStarter>();
    private readonly IGraphLoadStarter _graphLoadStarter = Substitute.For<IGraphLoadStarter>();
    private readonly IPaiStarter _paiStarter = Substitute.For<IPaiStarter>();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 9, 25, 12, 0, 0, TimeSpan.Zero));

    public ScanGateEvaluatorTests() =>
        _reconcileStarter.StartAsync(default, default, default).ReturnsForAnyArgs(true);

    private ScanGateEvaluator CreateEvaluator() =>
        new(_store, _probe, _reconcileStarter, _graphLoadStarter, _paiStarter, new ScanGateOptions(Grace), _time, NullLogger<ScanGateEvaluator>.Instance);

    private Task<ScanGateOutcome> EvaluateAsync() => CreateEvaluator().EvaluateAsync(new ScanGateRequest(_tenantId, _scanId));

    private WorkflowEntity ScanWorkflow(string type, (string Status, bool Terminal) row) => new()
    {
        Id = Guid.NewGuid(),
        ScanId = _scanId,
        WorkflowType = type,
        Status = row.Status,
        CompletedAt = row.Terminal ? _time.GetUtcNow() : null,
    };

    private void GivenSnapshot(
        IEnumerable<string>? taskStatuses = null,
        IReadOnlyDictionary<string, int>? fileCounts = null,
        IEnumerable<ScanFile>? pendingFiles = null,
        IEnumerable<(Guid ManifestId, string Type, string Status, bool Terminal)>? workflows = null,
        IEnumerable<(string Status, bool Terminal)>? reconciles = null,
        IEnumerable<(string Status, bool Terminal)>? graphLoads = null)
    {
        var workflowRows = (workflows ?? []).ToList();
        _store.LoadAsync(_tenantId, _scanId, Arg.Any<CancellationToken>()).Returns(new ScanGateSnapshot(
            [.. (taskStatuses ?? [ScanTaskStatus.Completed]).Select(status => new ScanTask { Id = Guid.NewGuid(), ScanId = _scanId, Status = status })],
            fileCounts ?? new Dictionary<string, int> { [ScanFileStatus.Ingested] = 1 },
            [.. pendingFiles ?? []],
            [.. workflowRows.Select(w => w.ManifestId).Distinct()],
            [.. workflowRows.Select(w => new WorkflowEntity
            {
                Id = Guid.NewGuid(),
                ScanManifestId = w.ManifestId,
                WorkflowType = w.Type,
                Status = w.Status,
                CompletedAt = w.Terminal ? _time.GetUtcNow() : null,
            })],
            // Succeeded by default, so a test about something else need not mention reconcile or the graph load.
            [.. (reconciles ?? [("completed", true)]).Select(r => ScanWorkflow(WorkflowTypes.Reconcile, r))],
            [.. (graphLoads ?? [("completed", true)]).Select(g => ScanWorkflow(WorkflowTypes.GraphLoad, g))]));
    }

    private static IEnumerable<(Guid, string, string, bool)> FullyProcessedManifest(Guid manifestId) =>
    [
        (manifestId, WorkflowTypes.Ingestion, "completed", true),
        (manifestId, WorkflowTypes.Atre, "completed", true),
        (manifestId, WorkflowTypes.Ownership, "completed", true),
    ];

    private ScanFile PendingFile(DateTimeOffset? expiresAt) => new()
    {
        Id = Guid.NewGuid(),
        ScanId = _scanId,
        ScanTaskId = Guid.NewGuid(),
        FileLocation = $"tenants/{_tenantId}/uploads/{_scanId}/{Guid.NewGuid()}/data_{Guid.NewGuid()}",
        Status = ScanFileStatus.Pending,
        UploadExpiresAt = expiresAt,
    };

    private async Task AssertPaiNotStartedAsync() =>
        await _paiStarter.DidNotReceiveWithAnyArgs().StartAsync(default, default, default);

    [Fact]
    public async Task EverythingDone_StampsTheScanAndStartsPai()
    {
        GivenSnapshot(workflows: FullyProcessedManifest(Guid.NewGuid()));

        Assert.Equal(ScanGateOutcome.Opened, await EvaluateAsync());

        await _store.Received(1).StampCompletedAsync(_tenantId, _scanId, _time.GetUtcNow(), Arg.Any<CancellationToken>());
        await _paiStarter.Received(1).StartAsync(_tenantId, _scanId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ATaskNotFinished_Waits()
    {
        GivenSnapshot(taskStatuses: [ScanTaskStatus.Completed, ScanTaskStatus.Pending], workflows: FullyProcessedManifest(Guid.NewGuid()));

        Assert.Equal(ScanGateOutcome.Waiting, await EvaluateAsync());
        await AssertPaiNotStartedAsync();
    }

    [Fact]
    public async Task AFailedTask_CountsAsFinished()
    {
        GivenSnapshot(taskStatuses: [ScanTaskStatus.Completed, ScanTaskStatus.Failed], workflows: FullyProcessedManifest(Guid.NewGuid()));

        Assert.Equal(ScanGateOutcome.Opened, await EvaluateAsync());
    }

    [Fact]
    public async Task NoTasks_Waits()
    {
        GivenSnapshot(taskStatuses: []);

        Assert.Equal(ScanGateOutcome.Waiting, await EvaluateAsync());
        await AssertPaiNotStartedAsync();
    }

    [Fact]
    public async Task AFileReceivedButNotIngested_Waits()
    {
        GivenSnapshot(
            fileCounts: new Dictionary<string, int> { [ScanFileStatus.Ingested] = 1, [ScanFileStatus.Received] = 1 },
            workflows: FullyProcessedManifest(Guid.NewGuid()));

        Assert.Equal(ScanGateOutcome.Waiting, await EvaluateAsync());
        await AssertPaiNotStartedAsync();
    }

    [Fact]
    public async Task APendingFileWhoseObjectExists_WaitsForThePipeline()
    {
        var file = PendingFile(_time.GetUtcNow().AddHours(-1));
        GivenSnapshot(pendingFiles: [file], workflows: FullyProcessedManifest(Guid.NewGuid()));
        _probe.ExistsAsync(file.FileLocation, Arg.Any<CancellationToken>()).Returns(true);

        Assert.Equal(ScanGateOutcome.Waiting, await EvaluateAsync());
        await _store.DidNotReceiveWithAnyArgs().MarkAbandonedAsync(default, default!, default);
        await AssertPaiNotStartedAsync();
    }

    /// <summary>Nothing emits an event for an upload that never happens — only redelivery looks again.</summary>
    [Fact]
    public async Task APendingFileMissingWithinItsUrlLifetimeAndGrace_AsksToBeRecheckedLater()
    {
        GivenSnapshot(
            pendingFiles: [PendingFile(_time.GetUtcNow() - Grace + TimeSpan.FromSeconds(1))],
            workflows: FullyProcessedManifest(Guid.NewGuid()));

        Assert.Equal(ScanGateOutcome.RecheckLater, await EvaluateAsync());
        await AssertPaiNotStartedAsync();
    }

    [Fact]
    public async Task APendingFileMissingPastItsUrlLifetimeAndGrace_IsAbandonedAndTheGateOpens()
    {
        var file = PendingFile(_time.GetUtcNow() - Grace - TimeSpan.FromSeconds(1));
        GivenSnapshot(pendingFiles: [file], workflows: FullyProcessedManifest(Guid.NewGuid()));

        Assert.Equal(ScanGateOutcome.Opened, await EvaluateAsync());
        await _store.Received(1).MarkAbandonedAsync(
            _tenantId, Arg.Is<IReadOnlyList<Guid>>(ids => ids.Count == 1 && ids[0] == file.Id), Arg.Any<CancellationToken>());
    }

    /// <summary>Other work in flight will emit its own event, so the message need not be kept.</summary>
    [Fact]
    public async Task AnUnexpiredUploadAlongsideOtherWork_WaitsWithoutARecheck()
    {
        var manifestId = Guid.NewGuid();
        GivenSnapshot(
            pendingFiles: [PendingFile(_time.GetUtcNow().AddMinutes(5))],
            workflows: [(manifestId, WorkflowTypes.Ingestion, "started", false)]);

        Assert.Equal(ScanGateOutcome.Waiting, await EvaluateAsync());
    }

    [Fact]
    public async Task AManifestWithNoIngestionRowYet_Waits()
    {
        var done = Guid.NewGuid();
        var notStarted = Guid.NewGuid();
        GivenSnapshot(workflows: FullyProcessedManifest(done));
        var snapshot = await _store.LoadAsync(_tenantId, _scanId);
        _store.LoadAsync(_tenantId, _scanId, Arg.Any<CancellationToken>())
            .Returns(snapshot with { ManifestIds = [done, notStarted] });

        Assert.Equal(ScanGateOutcome.Waiting, await EvaluateAsync());
        await AssertPaiNotStartedAsync();
    }

    [Fact]
    public async Task AFailedIngestion_IsNotExpectedToHaveAtreOrOwnership()
    {
        GivenSnapshot(workflows: [(Guid.NewGuid(), WorkflowTypes.Ingestion, "graph_load_failed", true)]);

        Assert.Equal(ScanGateOutcome.Opened, await EvaluateAsync());
    }

    [Fact]
    public async Task ACompletedIngestionWhoseAtreHasNotStarted_Waits()
    {
        var manifestId = Guid.NewGuid();
        GivenSnapshot(workflows:
        [
            (manifestId, WorkflowTypes.Ingestion, "completed", true),
            (manifestId, WorkflowTypes.Ownership, "completed", true),
        ]);

        Assert.Equal(ScanGateOutcome.Waiting, await EvaluateAsync());
        await AssertPaiNotStartedAsync();
    }

    [Fact]
    public async Task AFailedAtreOrOwnership_CountsAsTerminal()
    {
        var manifestId = Guid.NewGuid();
        GivenSnapshot(workflows:
        [
            (manifestId, WorkflowTypes.Ingestion, "completed", true),
            (manifestId, WorkflowTypes.Atre, "failed", true),
            (manifestId, WorkflowTypes.Ownership, "completed_with_errors", true),
        ]);

        Assert.Equal(ScanGateOutcome.Opened, await EvaluateAsync());
    }

    /// <summary>A retry reopens the row (status started, completion time cleared) until it finishes.</summary>
    [Fact]
    public async Task AnOwnershipRetryInProgress_Waits_ThenOpensOnceItFinishes()
    {
        var manifestId = Guid.NewGuid();
        GivenSnapshot(workflows:
        [
            (manifestId, WorkflowTypes.Ingestion, "completed", true),
            (manifestId, WorkflowTypes.Atre, "completed", true),
            (manifestId, WorkflowTypes.Ownership, "started", false),
        ]);

        Assert.Equal(ScanGateOutcome.Waiting, await EvaluateAsync());

        GivenSnapshot(workflows: FullyProcessedManifest(manifestId));

        Assert.Equal(ScanGateOutcome.Opened, await EvaluateAsync());
        await _paiStarter.Received(1).StartAsync(_tenantId, _scanId, Arg.Any<CancellationToken>());
    }

    private async Task AssertGraphLoadNotStartedAsync() =>
        await _graphLoadStarter.DidNotReceiveWithAnyArgs().StartAsync(default, default, default);

    [Fact]
    public async Task EveryManifestIngested_StampsIngestionAndStartsTheGraphLoad_ThenWaitsForIt()
    {
        GivenSnapshot(workflows: FullyProcessedManifest(Guid.NewGuid()), graphLoads: []);

        Assert.Equal(ScanGateOutcome.Waiting, await EvaluateAsync());

        await _store.Received(1).StampIngestionCompletedAsync(_tenantId, _scanId, _time.GetUtcNow(), Arg.Any<CancellationToken>());
        await _graphLoadStarter.Received(1).StartAsync(_tenantId, _scanId, Arg.Any<CancellationToken>());
        await AssertPaiNotStartedAsync();
        await _store.DidNotReceiveWithAnyArgs().StampCompletedAsync(default, default, default, default);
    }

    [Fact]
    public async Task AManifestStillIngesting_DoesNotStartTheGraphLoad()
    {
        GivenSnapshot(workflows: [(Guid.NewGuid(), WorkflowTypes.Ingestion, "started", false)], graphLoads: []);

        Assert.Equal(ScanGateOutcome.Waiting, await EvaluateAsync());
        await AssertGraphLoadNotStartedAsync();
        await _store.DidNotReceiveWithAnyArgs().StampIngestionCompletedAsync(default, default, default, default);
    }

    /// <summary>The load's own completion is an execution event that evaluates the gate again.</summary>
    [Fact]
    public async Task AGraphLoadStillRunning_Waits()
    {
        GivenSnapshot(workflows: FullyProcessedManifest(Guid.NewGuid()), graphLoads: [("started", false)]);

        Assert.Equal(ScanGateOutcome.Waiting, await EvaluateAsync());
        await AssertPaiNotStartedAsync();
    }

    /// <summary>The graph load does not wait for ATRE or Ownership: neither reads the graph. PAI waits for all three.</summary>
    [Fact]
    public async Task IngestionDone_StartsTheGraphLoad_WhileAtreAndOwnershipAreStillRunning()
    {
        var manifestId = Guid.NewGuid();
        GivenSnapshot(
            workflows: [(manifestId, WorkflowTypes.Ingestion, "completed", true), (manifestId, WorkflowTypes.Atre, "started", false)],
            graphLoads: [("completed", true)]);

        Assert.Equal(ScanGateOutcome.Waiting, await EvaluateAsync());
        await _graphLoadStarter.Received(1).StartAsync(_tenantId, _scanId, Arg.Any<CancellationToken>());
        await AssertPaiNotStartedAsync();
    }

    /// <summary>PAI runs over the graph: a failed load holds it back until the load is redriven and succeeds.</summary>
    [Fact]
    public async Task AFailedGraphLoad_HoldsPaiBack()
    {
        GivenSnapshot(workflows: FullyProcessedManifest(Guid.NewGuid()), graphLoads: [("failed", true)]);

        Assert.Equal(ScanGateOutcome.Waiting, await EvaluateAsync());
        await AssertPaiNotStartedAsync();
        await _store.DidNotReceiveWithAnyArgs().StampCompletedAsync(default, default, default, default);
    }

    /// <summary>The graph load writes reconcile's deletions, so it waits for reconcile to finish.</summary>
    [Fact]
    public async Task EveryManifestIngested_StartsReconcile_AndHoldsTheGraphLoadUntilReconcileFinishes()
    {
        GivenSnapshot(workflows: FullyProcessedManifest(Guid.NewGuid()), reconciles: [], graphLoads: []);

        Assert.Equal(ScanGateOutcome.Waiting, await EvaluateAsync());
        await _store.Received(1).StampIngestionCompletedAsync(_tenantId, _scanId, _time.GetUtcNow(), Arg.Any<CancellationToken>());
        await _reconcileStarter.Received(1).StartAsync(_tenantId, _scanId, Arg.Any<CancellationToken>());
        await AssertGraphLoadNotStartedAsync();

        GivenSnapshot(workflows: FullyProcessedManifest(Guid.NewGuid()), reconciles: [("started", false)], graphLoads: []);

        Assert.Equal(ScanGateOutcome.Waiting, await EvaluateAsync());
        await AssertGraphLoadNotStartedAsync();

        GivenSnapshot(workflows: FullyProcessedManifest(Guid.NewGuid()), reconciles: [("completed_with_proposals", true)], graphLoads: []);

        Assert.Equal(ScanGateOutcome.Waiting, await EvaluateAsync());
        await _graphLoadStarter.Received(1).StartAsync(_tenantId, _scanId, Arg.Any<CancellationToken>());
    }

    /// <summary>A failed reconcile deleted nothing; the scan carries on without it.</summary>
    [Fact]
    public async Task AFailedReconcile_StillLetsTheScanGoOnToPai()
    {
        GivenSnapshot(workflows: FullyProcessedManifest(Guid.NewGuid()), reconciles: [("failed", true)]);

        Assert.Equal(ScanGateOutcome.Opened, await EvaluateAsync());
        await _graphLoadStarter.Received(1).StartAsync(_tenantId, _scanId, Arg.Any<CancellationToken>());
        await _paiStarter.Received(1).StartAsync(_tenantId, _scanId, Arg.Any<CancellationToken>());
    }

    /// <summary>Where reconcile is not deployed there is no row to wait for: the gate goes straight to the graph load.</summary>
    [Fact]
    public async Task ReconcileNotDeployed_StartsTheGraphLoadWithoutWaiting()
    {
        _reconcileStarter.StartAsync(default, default, default).ReturnsForAnyArgs(false);
        GivenSnapshot(workflows: FullyProcessedManifest(Guid.NewGuid()), reconciles: [], graphLoads: []);

        Assert.Equal(ScanGateOutcome.Waiting, await EvaluateAsync());
        await _graphLoadStarter.Received(1).StartAsync(_tenantId, _scanId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AManifestStillIngesting_DoesNotStartReconcile()
    {
        GivenSnapshot(workflows: [(Guid.NewGuid(), WorkflowTypes.Ingestion, "started", false)], reconciles: [], graphLoads: []);

        Assert.Equal(ScanGateOutcome.Waiting, await EvaluateAsync());
        await _reconcileStarter.DidNotReceiveWithAnyArgs().StartAsync(default, default, default);
    }

    /// <summary>Every manifest failed: the load still runs (writing nothing) and closes, so the scan can finish.</summary>
    [Fact]
    public async Task EveryIngestionFailed_StillStartsTheGraphLoad()
    {
        GivenSnapshot(workflows: [(Guid.NewGuid(), WorkflowTypes.Ingestion, "resolve_edges_failed", true)], graphLoads: []);

        Assert.Equal(ScanGateOutcome.Waiting, await EvaluateAsync());
        await _graphLoadStarter.Received(1).StartAsync(_tenantId, _scanId, Arg.Any<CancellationToken>());
    }
}
