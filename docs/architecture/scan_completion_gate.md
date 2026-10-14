# Scan-Completion Gate

> Covers `backend/functions/Thor.ScanGate/` and the `scan_file` lifecycle it depends on — how a
> scan is judged ingested and complete, what each starts, and the infrastructure in
> `infra/src/modules/scan_gate`.

## 1. What it does

Ingestion, ATRE and Ownership all run **per manifest**, and a scan has as many manifests as the
upload batching happened to produce, so no single workflow knows when the scan is finished. The gate
answers that question on every event that could advance one, in two stages, and starts what each
stage unlocks exactly once.

**Stage one — ingested.** All of these hold:

1. every `scan_task` of the scan is `Completed` or `Failed`;
2. every `scan_file` of the scan is `Ingested`, `Failed` or `Abandoned` (§3);
3. every `scan_manifest` of the scan has a terminal ingestion `workflow` row.

The gate stamps `scan.ingestion_completed_at` (only if unset) and starts the scan's **reconcile** —
`{TenantId, ScanId}` as **`reconcile-{scanId}`** — which soft-deletes what the scan no longer saw
(`reconcile_workflow.md`). It waits until the scan's `reconcile` row is **terminal**, succeeded or
failed: a failed reconcile deleted nothing, and the scan carries on without it. Then it starts the
scan's **graph load** — the graph-load state machine with `{TenantId, ScanId}` under the execution
name **`graph-load-{scanId}`** — which writes what the scan changed, reconcile's deletions included,
into Neptune (`graph_load_workflow.md`). Where reconcile is not deployed
(`THOR_RECONCILE_STATE_MACHINE_ARN` empty) the gate starts the graph load straight away.

**Stage two — complete.** Additionally:

4. the scan's `graph-load` row is **`completed`** — a failed load holds PAI back until it is redriven
   and succeeds;
5. every manifest whose ingestion *succeeded* has a terminal ATRE row and a terminal Ownership row.

The gate stamps `scan.ingestion_completed_at` and `scan.analysis_completed_at` (each only if unset)
and starts the PAI state machine as **`pai-{scanId}`** with `{TenantId, ScanId}` — in an environment
that runs PAI. Where it does not (`THOR_PAI_STATE_MACHINE_ARN` empty), stage two only logs; the gate
still exists there, because stage one is what writes the graph.

**Terminal** means `workflow.completed_at` is set. `WorkflowLifecycle` stamps it on success and on
failure alike, and `ReopenAsync` clears it when a retry starts, so the gate needs no list of each
module's status strings. **Succeeded** means the status is `completed`. A failed ingestion never
starts ATRE or Ownership, so nothing more is awaited for that manifest, and the graph load writes
nothing for it. A failed ATRE or Ownership run counts as terminal, as it always has, so one failure
cannot stall PAI for the scan forever; the failure stays on that workflow's row.

The graph load does not wait for ATRE or Ownership — both read Postgres only — so they usually run
while the graph is being written. PAI waits for all three, and whichever finishes last triggers it.

The pinned execution names are the exactly-once guarantee: the gate runs concurrently and repeatedly
for one scan, and Step Functions rejects every start after the first with `ExecutionAlreadyExists`,
which the gate treats as success. Stage one's starts are issued on every evaluation from then on, which
is why the rejection is the common case. Whether reconcile has finished is read from its workflow row,
as the graph load's is — not inferred from what `StartExecution` answers.

## 2. What triggers it

Everything arrives on one SQS queue, `thor-<env>-scan-gate-sqs`, consumed by an event source mapping
with `ReportBatchItemFailures`:

| Source | Message | Status |
|---|---|---|
| EventBridge rule on *Step Functions Execution Status Change* (`SUCCEEDED`, `FAILED`, `TIMED_OUT`, `ABORTED`) for the ingestion, ATRE, Ownership and graph-load state machines, and reconcile's where deployed | the event; `detail.input` is the execution's own input | wired |
| Thor.TaskApi, when a task reaches a terminal status (`Services/ScanGateNotifier.cs`) | `{ "TenantId": …, "ScanId": … }` | wired |
| The gate itself, via redelivery | the same message, returned as a batch-item failure | wired |

The EventBridge rule needs no ASL change and also fires for executions that die without reaching
their Catch path. Ingestion's, reconcile's and the graph load's inputs carry `ScanId`; ATRE's and Ownership's carry
only `ScanManifestId`, which the gate resolves through `scan_manifest`. Messages are grouped so each
scan in a batch is evaluated once.

The Task API message matters more than it looks: without it the gate is evaluated only when an
execution ends, so a task that reports done after its scan's last manifest finished would leave the
scan waiting forever — its graph never written. A failure to send is logged by the Task API, not
retried (the task's status is already committed); the scan then waits for its next execution event.

## 3. `scan_file` — knowing every file that is still coming

A task can upload any number of files, and uploads reach a manifest through S3 → SQS → batching,
so a task marked done may still have files in flight. The gate closes that gap by knowing every file
before it can exist:

| Status | Written by | When |
|---|---|---|
| `Pending` | Thor.TaskApi `UploadService` | when it presigns the URL — before returning it — with `upload_expires_at` |
| `Received` | Thor.CreateManifest (best-effort), ingestion's list step | the object is in S3; upgrades `Pending` only, never downgrades |
| `Ingested` / `Failed` | ingestion's per-file step | last write wins, so a retried file converges |
| `Abandoned` | Thor.ScanGate | the URL was never used (below) |

A `Pending` file of a finished task is resolved with `HeadObject` (S3 is strongly consistent):

- **object exists** → it is on its way through the pipeline; wait. Its manifest's executions will
  emit the events that evaluate the gate again.
- **object missing, and `now > upload_expires_at + grace`** → mark it `Abandoned` and stop waiting.
  The grace (`THOR_SCAN_GATE_UPLOAD_GRACE_SECONDS`, default 900) covers a PUT that started just
  before expiry — S3 checks the signature when the request starts.
- **object missing, URL not yet expired** → wait, and if nothing else is outstanding return the
  message as a batch-item failure so it comes back after the queue's visibility timeout (300 s).
  An upload that never happens emits no event, so redelivery is the gate's only timer.
  20 receives × 300 s bounds this well past a 15-minute URL plus its grace.

A file whose key carries no task id did not come through the presign path and is not tracked,
as ingestion already did. Thor.CreateManifest's mark is best-effort: a failure there only costs the
gate a `HeadObject`, whereas throwing would stop the batch becoming a manifest.

## 4. Infrastructure (`infra/src/modules/scan_gate`)

Instantiated from `workflows.tf` whenever `var.workflows["graph-load"].enabled` — it starts every
scan's graph load, so it exists wherever graph-load does. PAI is optional: its ARN is passed only when
`var.workflows["pai"].enabled`, and the gate otherwise logs where it would have started PAI.
Reconcile likewise: its ARN is passed, and its state machine watched, only when
`var.workflows["reconcile"].enabled`; otherwise the gate goes straight to the graph load. It runs
on graph-load's workflow security group (it is graph-load's trigger, as CreateManifest is
ingestion's), which the RDS proxy already admits.

- In-VPC Lambda with no NAT: S3 through the gateway endpoint, Step Functions through the `states`
  interface endpoint. There is no SQS endpoint, which is why the handler never calls SQS.
- IAM: `sqs:ReceiveMessage/DeleteMessage/GetQueueAttributes` on its queue, `rds-db:connect`,
  `s3:GetObject` on `tenants/*` plus **`s3:ListBucket`** on the uploads bucket — without it S3
  answers `HeadObject` on a missing key with 403, not 404 — and `states:StartExecution` on the
  graph-load state machine and, where deployed, the reconcile and PAI state machines. The role carries the
  permissions boundary; the boundary is hand-managed, so confirm it allows these actions before
  first enabling.
- Thor.TaskApi sends to the queue through the VPC's `sqs` interface endpoint (its subnets have no
  NAT), with `sqs:SendMessage` on the queue (`modules/ecs`, `task_scan_gate_send`) and the URL in
  `THOR_SCAN_GATE_QUEUE_URL`. Both are built from the queue's naming rule at the root
  (`main.tf`, `scan_gate_queue_name`), not read off the module.
- Built by `scripts/publish-lambda-functions.sh` like every other function; `scan_gate_source_dir`
  points at its `publish/`.

## 5. Known gaps

- **A stuck row stalls the gate.** If `StartAtre`/`StartOwnership` land in ingestion's DLQ, no ATRE or
  Ownership row is ever created; if an ingestion Lambda is killed by its timeout, its row may stay
  in-progress (ingestion marks failure in-process, not from a Catch path). A manifest stuck in
  ingestion keeps the scan's graph from being written as well as PAI from starting. The gate logs what
  it is waiting on; `workflow.timeout_at` is the obvious hook for a follow-up, and an alarm on scans
  waiting too long the cheap first step.
- **A failed graph load holds PAI back** until it is redriven and succeeds; nothing alerts on it yet
  beyond the gate's warning log and the execution's own failure.
- **A reconcile run that never closes its row holds the graph load back.** A reconcile execution
  stopped by hand, timed out, or whose RecordFailure itself fails leaves its row open (or never
  creates it), and the gate waits on it. Graph-load has the same exposure for PAI; here it is the
  scan's whole graph. Closing the row (`completed_at`) by hand releases the scan.
- **Freshness.** A scan's graph is written only once the whole scan is ingested.
- **Existing tenants' schema.** `scan_file.upload_expires_at` reaches new tenants through the
  regenerated migration; existing tenants need Blue/Green, as with every tenant-schema change.
- **Unverified against AWS:** that the status-change event's `detail.input` is present for these
  small inputs (it is omitted only past the size limit) — covered by the parser, not yet observed.

## 6. Key file reference

| Concern | File |
|---|---|
| Decision | `Thor.ScanGate.Core/ScanGateEvaluator.cs` |
| Batch resolution, de-dup, redelivery | `Thor.ScanGate.Core/ScanGateBatchProcessor.cs` |
| Tenant reads/writes | `Thor.ScanGate.Core/TenantScanGateStore.cs` |
| Message shapes | `Thor.ScanGate.Function/GateMessageParser.cs` |
| Reconcile start | `Thor.ScanGate.Function/StepFunctionsReconcileStarter.cs`, `DisabledReconcileStarter.cs` |
| Graph-load start | `Thor.ScanGate.Function/StepFunctionsGraphLoadStarter.cs` |
| PAI start | `Thor.ScanGate.Function/StepFunctionsPaiStarter.cs`, `DisabledPaiStarter.cs` |
| Task completion message | `backend/services/Thor.TaskAPI/Services/ScanGateNotifier.cs` |
| Presign registration | `backend/services/Thor.TaskAPI/Services/UploadService.cs` |
| Received marking | `backend/functions/Thor.CreateManifest/src/Thor.CreateManifest.Core/ManifestBatchProcessor.cs` |
| `scan_file` writes | `backend/shared/Thor.DataLayer/Repositories/Tenant/ScanFileRepository.cs` |
| Infrastructure | `infra/src/modules/scan_gate/`, `infra/src/workflows.tf` (`scan_gate`) |
