using Microsoft.Extensions.Logging;
using Thor.DataLayer.Models.Tenants;
using Thor.Workflows.Abstractions;

namespace Thor.ScanGate.Core;

/// <summary>
/// Decides how far a scan has got, in two stages, and starts what each stage unlocks:
/// <list type="number">
/// <item><b>Ingested</b> — every task finished, every file ingested and every manifest's ingestion
/// terminal. Stamps the scan and starts reconcile, which deletes what the scan no longer saw; once
/// reconcile is terminal (or where it is not deployed), starts the graph load, which writes the scan —
/// and those deletions — into Neptune.</item>
/// <item><b>Complete</b> — that graph load succeeded, and ATRE and Ownership are terminal for every
/// manifest ingestion completed. Stamps the scan and starts PAI.</item>
/// </list>
///
/// <para>Runs on every event that could advance a scan, concurrently and more than once for the same
/// scan, so nothing here may assume it is the only or the last evaluation: every start is idempotent
/// per scan (<see cref="IReconcileStarter"/>, <see cref="IGraphLoadStarter"/>, <see cref="IPaiStarter"/>)
/// and both stamps keep the first time.</para>
/// </summary>
public sealed class ScanGateEvaluator(
    IScanGateStore store,
    IUploadObjectProbe uploadObjectProbe,
    IReconcileStarter reconcileStarter,
    IGraphLoadStarter graphLoadStarter,
    IPaiStarter paiStarter,
    ScanGateOptions options,
    TimeProvider timeProvider,
    ILogger<ScanGateEvaluator> logger)
{
    /// <summary>
    /// The status <c>WorkflowLifecycle.MarkCompletedAsync</c> writes by default — what ingestion and
    /// the graph load record when their run succeeded. Any other status with a completion time is a
    /// failure.
    /// </summary>
    private const string SucceededStatus = "completed";

    /// <summary>What must be terminal for a manifest whose ingestion succeeded.</summary>
    private static readonly string[] DownstreamWorkflowTypes = [WorkflowTypes.Atre, WorkflowTypes.Ownership];

    public async Task<ScanGateOutcome> EvaluateAsync(ScanGateRequest request, CancellationToken cancellationToken = default)
    {
        var (tenantId, scanId) = request;
        var snapshot = await store.LoadAsync(tenantId, scanId, cancellationToken);

        if (snapshot.Tasks.Count == 0)
        {
            logger.LogWarning("Scan {ScanId} has no tasks; nothing can complete it", scanId);
            return ScanGateOutcome.Waiting;
        }

        var unfinishedTasks = snapshot.Tasks.Count(t => t.Status is not (ScanTaskStatus.Completed or ScanTaskStatus.Failed));
        if (unfinishedTasks > 0)
        {
            logger.LogInformation(
                "Scan {ScanId} gate waiting: {UnfinishedTasks} of {TotalTasks} task(s) not finished",
                scanId, unfinishedTasks, snapshot.Tasks.Count);
            return ScanGateOutcome.Waiting;
        }

        var uploads = await ResolvePendingUploadsAsync(tenantId, snapshot.PendingFiles, cancellationToken);
        var receivedFiles = snapshot.FileCountsByStatus.GetValueOrDefault(ScanFileStatus.Received);
        var manifests = CountUnfinishedManifests(snapshot);

        if (uploads.InFlight > 0 || uploads.NotYetExpired > 0 || receivedFiles > 0 || manifests.AwaitingIngestion > 0)
        {
            logger.LogInformation(
                "Scan {ScanId} gate waiting on ingestion: {InFlightUploads} upload(s) in S3 not yet received, {UnexpiredUploads} upload URL(s) not yet expired, {ReceivedFiles} file(s) not yet ingested, {AwaitingIngestion} manifest(s) awaiting ingestion",
                scanId, uploads.InFlight, uploads.NotYetExpired, receivedFiles, manifests.AwaitingIngestion);

            // Only an unexpired URL needs the message kept: everything else ends in an event
            // (an upload creates a manifest, and every execution emits a status change) that
            // evaluates the gate again. An upload that never happens emits nothing.
            var onlyTimerCanResolve = uploads.NotYetExpired > 0
                && uploads.InFlight == 0 && receivedFiles == 0 && manifests.AwaitingIngestion == 0;
            return onlyTimerCanResolve ? ScanGateOutcome.RecheckLater : ScanGateOutcome.Waiting;
        }

        // Stage one: every manifest is in Postgres, so reconcile can delete what the scan no longer saw,
        // and then the scan's graph can be written. Issued on every evaluation from here on; every start
        // after the first is a no-op.
        await store.StampIngestionCompletedAsync(tenantId, scanId, timeProvider.GetUtcNow(), cancellationToken);
        if (await reconcileStarter.StartAsync(tenantId, scanId, cancellationToken) && !IsTerminal(snapshot.Reconciles))
        {
            // Its completion is an execution status change, which evaluates the gate again. A failed
            // reconcile is terminal too: it deletes nothing, and the scan carries on without it.
            logger.LogInformation("Scan {ScanId} gate waiting on reconcile before the graph load", scanId);
            return ScanGateOutcome.Waiting;
        }

        await graphLoadStarter.StartAsync(tenantId, scanId, cancellationToken);

        var graphLoadDone = IsTerminal(snapshot.GraphLoads);
        if (!graphLoadDone || manifests.AwaitingDownstream > 0)
        {
            // Both end in an execution status change, which evaluates the gate again.
            logger.LogInformation(
                "Scan {ScanId} gate waiting on analysis: graph load {GraphLoadState}, {AwaitingDownstream} manifest(s) awaiting ATRE/Ownership",
                scanId, graphLoadDone ? "done" : "not finished", manifests.AwaitingDownstream);
            return ScanGateOutcome.Waiting;
        }

        if (snapshot.GraphLoads.Any(w => w.Status != SucceededStatus))
        {
            // PAI runs over the graph, so a failed load holds it back. Redriving the graph load ends in
            // another execution status change, which evaluates the gate again.
            logger.LogWarning(
                "Scan {ScanId}: graph load ended {Status}; PAI waits until it is redriven and succeeds",
                scanId, snapshot.GraphLoads[0].Status);
            return ScanGateOutcome.Waiting;
        }

        await store.StampCompletedAsync(tenantId, scanId, timeProvider.GetUtcNow(), cancellationToken);
        await paiStarter.StartAsync(tenantId, scanId, cancellationToken);

        logger.LogInformation(
            "Scan {ScanId} gate opened: {TaskCount} task(s), {ManifestCount} manifest(s), the graph load, ATRE and Ownership done; PAI started",
            scanId, snapshot.Tasks.Count, snapshot.ManifestIds.Count);
        return ScanGateOutcome.Opened;
    }

    /// <summary>
    /// A Pending file's task has finished, so the connector will upload nothing more for it: the
    /// object either exists (it is on its way through the pipeline), or never will once the URL
    /// and its grace have passed. S3 is strongly consistent, so a missing object is really missing.
    /// </summary>
    private async Task<(int InFlight, int NotYetExpired)> ResolvePendingUploadsAsync(
        Guid tenantId, IReadOnlyList<ScanFile> pendingFiles, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var inFlight = 0;
        var notYetExpired = 0;
        var abandoned = new List<Guid>();

        foreach (var file in pendingFiles)
        {
            if (await uploadObjectProbe.ExistsAsync(file.FileLocation, cancellationToken))
            {
                inFlight++;
            }
            else if (file.UploadExpiresAt is { } expiresAt && now <= expiresAt + options.UploadGrace)
            {
                notYetExpired++;
            }
            else
            {
                abandoned.Add(file.Id);
            }
        }

        if (abandoned.Count > 0)
        {
            await store.MarkAbandonedAsync(tenantId, abandoned, cancellationToken);
            logger.LogInformation("Marked {AbandonedCount} never-uploaded file(s) abandoned", abandoned.Count);
        }

        return (inFlight, notYetExpired);
    }

    /// <summary>
    /// Terminal means a completion time is recorded — every module stamps one on success and on
    /// failure alike, and a reopened retry clears it. A manifest with no ingestion row yet is not
    /// terminal, and neither is one whose ATRE or Ownership row has not been created yet.
    /// </summary>
    private static (int AwaitingIngestion, int AwaitingDownstream) CountUnfinishedManifests(ScanGateSnapshot snapshot)
    {
        var byManifest = snapshot.ManifestWorkflows
            .Where(w => w.ScanManifestId is not null)
            .ToLookup(w => (w.ScanManifestId!.Value, w.WorkflowType));

        var awaitingIngestion = 0;
        var awaitingDownstream = 0;

        foreach (var manifestId in snapshot.ManifestIds)
        {
            var ingestion = byManifest[(manifestId, WorkflowTypes.Ingestion)].ToList();
            if (!IsTerminal(ingestion))
            {
                awaitingIngestion++;
                continue;
            }

            // A failed ingestion never starts ATRE or Ownership, so there is nothing more to wait for.
            if (ingestion.All(w => w.Status != SucceededStatus))
            {
                continue;
            }

            if (!DownstreamWorkflowTypes.All(type => IsTerminal(byManifest[(manifestId, type)].ToList())))
            {
                awaitingDownstream++;
            }
        }

        return (awaitingIngestion, awaitingDownstream);
    }

    private static bool IsTerminal(IReadOnlyList<WorkflowEntity> rows) =>
        rows.Count > 0 && rows.All(w => w.CompletedAt is not null);
}
