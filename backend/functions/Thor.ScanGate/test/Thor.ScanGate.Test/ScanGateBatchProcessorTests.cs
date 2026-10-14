using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Thor.DataConnectionManager.Exceptions;
using Thor.DataLayer.Models.Tenants;
using Thor.ScanGate.Core;

namespace Thor.ScanGate.Test;

/// <summary>Covers <see cref="ScanGateBatchProcessor"/>'s resolution, de-duplication and redelivery.</summary>
public class ScanGateBatchProcessorTests
{
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly IScanGateStore _store = Substitute.For<IScanGateStore>();
    private readonly IUploadObjectProbe _probe = Substitute.For<IUploadObjectProbe>();
    private readonly IReconcileStarter _reconcileStarter = Substitute.For<IReconcileStarter>();
    private readonly IGraphLoadStarter _graphLoadStarter = Substitute.For<IGraphLoadStarter>();
    private readonly IPaiStarter _paiStarter = Substitute.For<IPaiStarter>();

    private ScanGateBatchProcessor CreateProcessor() =>
        new(_store,
            new ScanGateEvaluator(_store, _probe, _reconcileStarter, _graphLoadStarter, _paiStarter, new ScanGateOptions(TimeSpan.FromMinutes(15)), TimeProvider.System, NullLogger<ScanGateEvaluator>.Instance),
            NullLogger<ScanGateBatchProcessor>.Instance);

    /// <summary>A scan with one unfinished task: the evaluator waits without asking for a recheck.</summary>
    private void GivenWaitingScan(Guid scanId) =>
        _store.LoadAsync(_tenantId, scanId, Arg.Any<CancellationToken>()).Returns(new ScanGateSnapshot(
            [new ScanTask { Id = Guid.NewGuid(), ScanId = scanId, Status = ScanTaskStatus.Pending }],
            new Dictionary<string, int>(), [], [], [], [], []));

    /// <summary>A scan waiting only on an unexpired upload URL: the evaluator asks for a recheck.</summary>
    private void GivenScanAwaitingAnUpload(Guid scanId) =>
        _store.LoadAsync(_tenantId, scanId, Arg.Any<CancellationToken>()).Returns(new ScanGateSnapshot(
            [new ScanTask { Id = Guid.NewGuid(), ScanId = scanId, Status = ScanTaskStatus.Completed }],
            new Dictionary<string, int>(),
            [new ScanFile { Id = Guid.NewGuid(), ScanId = scanId, FileLocation = "k", Status = ScanFileStatus.Pending, UploadExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5) }],
            [], [], [], []));

    [Fact]
    public async Task SeveralMessagesForOneScan_EvaluateItOnce()
    {
        var scanId = Guid.NewGuid();
        GivenWaitingScan(scanId);

        var retry = await CreateProcessor().ProcessAsync(
        [
            new GateMessage("m1", _tenantId, scanId, null),
            new GateMessage("m2", _tenantId, scanId, Guid.NewGuid()),
        ]);

        Assert.Empty(retry);
        await _store.Received(1).LoadAsync(_tenantId, scanId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AManifestOnlyMessage_IsResolvedToItsScan()
    {
        var (scanId, manifestId) = (Guid.NewGuid(), Guid.NewGuid());
        _store.FindScanIdForManifestAsync(_tenantId, manifestId, Arg.Any<CancellationToken>()).Returns(scanId);
        GivenWaitingScan(scanId);

        await CreateProcessor().ProcessAsync([new GateMessage("m1", _tenantId, null, manifestId)]);

        await _store.Received(1).LoadAsync(_tenantId, scanId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AnUnknownManifest_IsDropped()
    {
        _store.FindScanIdForManifestAsync(_tenantId, Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns((Guid?)null);

        var retry = await CreateProcessor().ProcessAsync([new GateMessage("m1", _tenantId, null, Guid.NewGuid())]);

        Assert.Empty(retry);
        await _store.DidNotReceiveWithAnyArgs().LoadAsync(default, default, default);
    }

    [Fact]
    public async Task AScanAwaitingAnUpload_HasAllItsMessagesRedelivered()
    {
        var scanId = Guid.NewGuid();
        GivenScanAwaitingAnUpload(scanId);

        var retry = await CreateProcessor().ProcessAsync(
        [
            new GateMessage("m1", _tenantId, scanId, null),
            new GateMessage("m2", _tenantId, scanId, null),
        ]);

        Assert.Equal(["m1", "m2"], retry.Order());
    }

    [Fact]
    public async Task AFailedEvaluation_IsRedelivered_WithoutDisturbingOtherScans()
    {
        var (failing, fine) = (Guid.NewGuid(), Guid.NewGuid());
        _store.LoadAsync(_tenantId, failing, Arg.Any<CancellationToken>()).ThrowsAsync(new TimeoutException("db"));
        GivenWaitingScan(fine);

        var retry = await CreateProcessor().ProcessAsync(
        [
            new GateMessage("bad", _tenantId, failing, null),
            new GateMessage("good", _tenantId, fine, null),
        ]);

        Assert.Equal(["bad"], retry);
    }

    [Fact]
    public async Task AnUnknownTenant_IsDroppedRatherThanRetried()
    {
        var scanId = Guid.NewGuid();
        _store.LoadAsync(_tenantId, scanId, Arg.Any<CancellationToken>()).ThrowsAsync(new TenantNotFoundException(_tenantId));

        var retry = await CreateProcessor().ProcessAsync([new GateMessage("m1", _tenantId, scanId, null)]);

        Assert.Empty(retry);
    }
}
