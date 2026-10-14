# Graph-Load Workflow

> Covers `backend/workflows/graph-load/Thor.Workflows.GraphLoad/` — how a scan's changes reach the
> Neptune graph, once per scan, and where it sits between ingestion and PAI — and its state machine,
> `infra/src/modules/workflow/asl/graph-load.asl.json`. One of the ADR §16 workflow modules:
> independent, additive, tenant-scoped, idempotent.

## 1. What this module does

Writes everything one scan changed into the tenant's partition of the shared Neptune cluster —
accounts, groups and assets as vertices, resolved relationships as edges — and removes what the
scan soft-deleted. PAI for the scan starts after it, from the scan-completion gate (§6).

It reads Postgres only. Ingestion (`ingestion_workflow.md`) leaves the canonical rows, the resolved
`tenant.edge` rows and an `ingest_change_event` for each one it changed, tagged with the manifest's
id; reconcile (`reconcile_workflow.md`) adds a manifest-less `deleted` event for each entity and edge
it soft-deleted because the scan no longer saw it. This module turns those into graph writes. Nothing
is staged between them.

It runs **once per scan**, started by the scan-completion gate (`scan_completion_gate.md`) when every
task of the scan has finished, every manifest's ingestion row is closed and — where reconcile is
deployed — the scan's reconcile row is closed too, succeeded or failed. Per scan rather than per
manifest because, written per manifest, each manifest re-wrote the edge endpoints its siblings had not
written yet — about 3.3M duplicate vertex rows on a 1.7M-entity dev scan.

It uses batched Gremlin upserts, not Neptune's bulk loader. The loader runs one job at a time per
cluster from a FIFO queue of at most 64, so every scan of every tenant waited behind every other.
Upserts have no queue: concurrent scans, any tenant's, share the writer's worker threads.

## 2. Deployment shape & entry points

One image (`backend/workflows/Dockerfile`, `--build-arg WORKFLOW=GraphLoad`), five steps, all Lambda:
start-run and next-wave hand the state machine the wave it maps over, which `ecs:runTask.sync` has no
channel for, and a Fargate task per chunk would pay a container cold start each time.

| `THOR_STEP` | Class | Purpose |
|---|---|---|
| `start-run` | `GraphLoadStartRunStep` | Open the scan's row, list what the scan changed as files of ids, write the plan, hand back wave 0 |
| `write-chunk` | `GraphLoadWriteChunkStep` | The Map body: read one chunk's rows from Postgres by id, upsert them into Neptune |
| `next-wave` | `GraphLoadNextWaveStep` | Read the plan, hand back the next wave or end the loop |
| `finalize` | `GraphLoadFinalizeStep` | Remove what the scan soft-deleted, close the row |
| `record-failure` | `GraphLoadRecordFailureStep` | Close the row as failed, from the Catch path |

`GraphLoadRequest(TenantId, ScanId)` is the execution input. `TenantId` is trusted as verified
upstream — the gate resolved the tenant's routing before starting the run — and every tenant database
connection validates it again.

## 3. End-to-end flow

```
scan-completion gate ── StartExecution(graph-load-{scanId}, {TenantId, ScanId})
   ▼
StartRun ── open the scan's graph-load row; list ids from Postgres; write ids/*.json and plan.json
   ▼
MoreWaves ⇄ WriteWave (Distributed Map, one item per id file) → NextWave
   │          every vertex wave first, then every edge wave
   ▼
Finalize ── drop soft-deleted vertices/edges, close the row `completed`
   ▼
WorkflowSucceeded

any failure → RecordFailure (row `failed`) → WorkflowFailed

either end is an execution event the gate watches; it starts PAI (§6)
```

## 4. What gets written — `Persistence/GraphLoadWriteSet.cs`

"Changed" means named by an `ingest_change_event` of the scan from a manifest whose ingestion
**completed**, or by one of the scan's manifest-less events, which are reconcile's and always count.
A manifest whose ingestion failed contributes nothing — ATRE and Ownership never ran for it either —
and the plan records which manifests counted, so the deletes at the end are taken over exactly the
same ones.

The row's *current* state decides what happens to it: live rows are written, soft-deleted ones
removed. Reading current state rather than a snapshot taken at ingestion means a later scan that
changed the same rows first is never undone by this one.

- **Vertices** (`account`, `grp`, `asset`): the changed live rows, **plus every endpoint of a changed
  live edge**. An edge into a group from an earlier scan writes that group again. `mergeE` fails a
  whole request over one missing endpoint, and re-upserting an existing vertex is idempotent and cheap,
  so this is simpler and safer than asking Neptune which endpoints it lacks.
- **Edges**: the changed live edges whose two endpoints are both live and of a kind the graph holds.
  An edge into a deleted, missing or entitlement vertex is not written; entitlements are not graph
  vertices.
- **Deletes** (finalize): changed accounts, groups and assets that are now soft-deleted (`drop()`
  removes their incident edges too), and changed edges that are now soft-deleted — a membership
  removed, as the edge gate records it — **whose two endpoints are both still live**. Reconcile
  soft-deletes every edge of each entity it deletes; those go with their vertex's `drop()`, so
  dropping them one by one would be most of finalize's work for nothing. Only reconcile deletes
  assets; a deleted asset is also left out of the vertex writes and as an edge endpoint.

The queries are shaped for millions of rows: each vertex set is one `IN` over a `UNION ALL`, which
Postgres plans as a join that spills to disk if it must — three `IN`s joined by `OR` would each need
their hash table to fit in `work_mem` or fall back to re-running per row. Edge endpoint checks are
primary-key probes, two per changed edge.

## 5. Chunks, waves and the plan

**start-run** streams each set's ids in a stable order — vertices by id, edges by target vertex then
id, so the edges into one large group mostly share a chunk and do not conflict across chunks — and
writes a JSON file every `THOR_GRAPH_LOAD_IDS_PER_CHUNK` ids (default 10,000) to the graph-load bucket
(`THOR_GRAPH_LOAD_BUCKET`) under `graph-load/{tenant}/{scan}/ids/`. Ids rather than rows: a Map item is
capped at 256 KB, and reading the rows in each chunk spreads the Postgres reads across the wave instead
of holding a whole scan in one invocation. Ids rather than an offset into the query: the write set is a
join that is cheap to run once and expensive to re-run per chunk.

The plan — every vertex chunk, then every edge chunk, and the manifests counted — is written last, to
`graph-load/{tenant}/{scan}/plan.json`; the execution's 256 KB state cap rules out carrying it. Keys are
fixed per scan, so a re-run of start-run rewrites what it names and the plan never names another
attempt's file. The bucket's lifecycle (14 days) expires the rest; the role cannot delete.

**Waves** follow ATRE's pattern: a Distributed Map over `THOR_GRAPH_LOAD_CHUNKS_PER_WAVE` chunks with
`MaxConcurrency` `map_max_concurrency`, then next-wave reads the plan for the next wave. Every vertex
wave precedes every edge wave and no wave mixes them — `mergeE` needs both endpoints to exist, and a
wave is the only barrier the state machine has.

**write-chunk** checks the key sits under the request's own tenant and scan prefix (fails closed
otherwise), reads its ids, loads the rows that are still live — a row soft-deleted since start-run
listed it is skipped — and hands them to `Thor.Graph`'s `GraphBatchWriter`:

- chained `mergeV`/`mergeE` upserts matched by id, about 200 records per request (AWS's guidance for
  Neptune), `THOR_GRAPH_LOAD_WRITE_PARALLELISM` requests in flight per chunk;
- GraphSON 3, which every `Thor.Graph` client speaks — GraphSON 2's maps cannot be keyed by
  `T.id`/`T.label`/`Direction`;
- retries `ConcurrentModificationException`, throttling, writer failover and dropped connections with
  jittered exponential backoff.

No chunk failure is tolerated: the Map item retries three times, then the run fails. A lost vertex
chunk would fail every edge chunk touching it, so tolerating it would trade one clear failure for many
unclear ones.

**A scan's share of the writer** is `map_max_concurrency × THOR_GRAPH_LOAD_WRITE_PARALLELISM` requests
in flight. Concurrent scans share the writer rather than queueing, so several large initial loads at
once slow every tenant down, but none waits behind another. Each chunk also holds one tenant database
connection, so `map_max_concurrency` bounds this run's connections through the RDS proxy too.

## 6. PAI

PAI needs the scan's graph and ATRE's and Ownership's results, and those finish independently —
ATRE and Ownership are started per manifest by ingestion and usually run while the graph is being
written. So PAI is started by the scan-completion gate, the one component that sees all of them
finish, rather than by this workflow: once this scan's graph-load row is `completed` **and** ATRE and
Ownership are terminal for every manifest whose ingestion succeeded, the gate starts `pai-{scanId}`
(`scan_completion_gate.md` §1). Whichever of the three finishes last triggers it; nothing polls.

A failed graph load holds PAI back: its row says `failed` until the execution is redriven and
Finalize closes it `completed`, and that run's end is the event that lets the gate start PAI.

## 7. Idempotency & tenant isolation

- **One run per scan.** The gate starts it as `graph-load-{scanId}`; every later start is rejected
  with `ExecutionAlreadyExists`. The row's `RunId` is derived from `(TenantId, ScanId)` with this
  module's own namespace (`GraphLoadSettings`), so a retry finds the row the first attempt opened and
  `ReopenAsync` clears its last failure.
- **Every write is an upsert by id** (`GraphIds`: `{tenantId}:{type}:{id}`, edges
  `{tenantId}:edge:{id}`), so a retried chunk, a re-run wave or a redriven execution finds what an
  earlier attempt wrote instead of duplicating it.
- **Deletes come last**, after every write succeeded, so a run that fails part-way removes nothing —
  the recoverable state.
- **Tenant isolation** is structural in the graph — a vertex cannot be addressed without its tenant
  id — and every S3 key a step reads is checked against the request's tenant and scan first.

A failed run is **redriven**, not restarted: the pinned name blocks a second execution for the scan.
Redrive resumes from the failed state; the bucket keeps the plan and ids for 14 days.

## 8. Configuration

| Setting | Where | Default |
|---|---|---|
| `THOR_GRAPH_LOAD_IDS_PER_CHUNK` | `settings.ids_per_chunk` | 10000 |
| `THOR_GRAPH_LOAD_CHUNKS_PER_WAVE` | `settings.chunks_per_wave` | 8 — keep equal to `map_max_concurrency` |
| `THOR_GRAPH_LOAD_WRITE_PARALLELISM` | `settings.write_parallelism` | 4 |
| `map_max_concurrency` (ASL) | `settings.map_max_concurrency` | 8 |

All under `var.workflows["graph-load"].settings` in the environment's `terragrunt.hcl`, read in
`infra/src/workflow_definitions.tf`.

## 9. Known gaps

- **Throughput is unmeasured on Neptune.** Batched upserts have been tested against TinkerPop Gremlin
  Server, not against Neptune Serverless, and AWS positions the bulk loader as the path for large
  volumes. The loader role and its bucket grants are kept in Terraform as the fallback until a dev run
  of a 1.7M-entity scan settles it. How Neptune's NCUs map to writer worker threads is undocumented, so
  the dials above are tuned empirically.
- **Freshness.** The graph reflects a scan only once the whole scan is ingested.
- **Conflicts on hot vertices.** Sorting edges by target and retrying mitigate them; the effect on an
  AD-scale tenant is unmeasured.
- **`ingest_change_event` has no index on `scan_id`.** start-run's queries scan it once per set; that
  grows with the table and wants an index on `(scan_id, entity_type)`.
- **A large reconcile deletion is dropped in one invocation.** Finalize drops vertices in a single
  Lambda; the 5% default threshold bounds an ordinary scan's deletions, but a source whose threshold
  was raised, or an approved over-threshold proposal once that exists, could approach the 15-minute
  timeout. Unmeasured.

## 10. Key files

| Concern | File |
|---|---|
| Steps | `Steps/GraphLoad*Step.cs`, `Steps/GraphDeleteExecutor.cs` |
| What gets written | `Persistence/GraphLoadWriteSet.cs`, `Persistence/GraphVertexProjection.cs` |
| Plan, keys, waves | `GraphLoadPlan.cs`, `GraphLoadKeys.cs`, `GraphLoadWavePlanner.cs` |
| Dials, run id, Neptune options | `GraphLoadSettings.cs` |
| Batched upserts | `backend/shared/Thor.Graph/GraphBatchWriter.cs` |
| State machine | `infra/src/modules/workflow/asl/graph-load.asl.json` |
| Terraform | `infra/src/workflow_definitions.tf` (`"graph-load"`) |
| Tests | `backend/workflows/graph-load/Thor.Workflows.GraphLoad.Tests/` (Testcontainers Postgres) |
