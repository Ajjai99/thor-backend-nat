# Reconcile Workflow

> Covers `backend/workflows/reconcile/Thor.Workflows.Reconcile/` — how entities a source no longer has
> are found and soft-deleted, once per scan, between ingestion and the graph load — its state machine,
> `infra/src/modules/workflow/asl/reconcile.asl.json`, and the seen-ledger ingestion writes for it. One
> of the ADR §16 workflow modules: independent, additive, tenant-scoped, idempotent.
>
> This is the only workflow in Thor that destroys data. Read §4 and §10 before changing it.

## 1. What this module does

Connectors never report a deletion: every normalizer emits `IsDeleted: false`. Without this module a
leaver stays an account forever, a decommissioned safe stays an asset forever, and every hygiene number
is inflated by entities that no longer exist. ADR §12 specified the behaviour — canonical keys absent
from the current scan are deletes — and this is it.

Once a scan's ingestion is complete, reconcile compares each source's live canonical rows with what that
scan actually saw and acts on the difference, per source and entity type (`account`, `grp`, `asset`,
`entitlement`):

- **within the source's threshold** — soft-delete the absent rows, with their edges, aliases, parked
  refs and active owners, and a `deleted` change event for each;
- **over it** — touch nothing, flag the rows as *proposed* for deletion, and record why.

Every decision, skipped sources included, lands in `tenant.reconcile_decision` — the record an admin
view will read (§7).

**The whole module rests on one property: connector exports are full dumps.** A live row of a source
whose tasks all completed, absent from every manifest of the scan, is one the source no longer has. A
delta-style connector would break that, and must be excluded explicitly before it ships (§10).

## 2. Where it sits

```
ingestion (per manifest) ──► scan-completion gate, stage one: every manifest ingested
                                   │
                                   ├─► reconcile-{scanId}        soft-deletes what the scan did not see
                                   │      (gate waits for its row to be terminal — succeeded or failed)
                                   │
                                   └─► graph-load-{scanId}       writes the scan's changes into Neptune,
                                                                 and drops reconcile's deleted vertices
                                                                       │
                       ATRE + Ownership terminal + graph load ok ──► PAI
```

Reconcile runs **before** the graph load so that one load applies both what the scan changed and what it
deleted; the graph load's finalize drops the deleted vertices (`graph_load_workflow.md` §4). That is why
this module never talks to Neptune. A **failed** reconcile is terminal too: it deleted nothing (or
whole windows only, §6), and the scan carries on without it — absences it missed are found by the next
scan. Where reconcile is not deployed, the gate goes straight to the graph load
(`scan_completion_gate.md` §1).

## 3. The seen-ledger — `tenant.scan_seen_entity`

`(scan_id, source_id, entity_type, native_id)`, primary key on all four. Written by **ingestion**:
every promotion batch inserts a row for every entity in the batch, from the batch's deduplicated staged
rows, `ON CONFLICT DO NOTHING` (`Promoter.cs`, the `seen` CTE). Absence is an anti-join against it.

- **Every row the export contained, not every row that changed.** Promotion writes only rows whose
  `content_hash` differs, so `updated_at` means *last changed*, not *last seen* — an account unchanged
  for a year is in every export. The ledger reads from the batch's input, not the upsert's `RETURNING`.
  `PromoteAccountsAsync_UnchangedAccountInALaterScan_IsStillRecordedAsSeen` pins this.
- **A separate table, not a `last_seen_scan_id` column.** Postgres has no in-place update: stamping 1M
  `account` rows rewrites 1M whole tuples, `raw_attributes` included, every scan.
- **`LOGGED`.** An `UNLOGGED` table is truncated on crash recovery, and an empty ledger reads as
  *everything is absent*.

Finalize drops the scan's ledger. Until then a failed run can be redriven.

## 4. Eligibility — `Scoping/SourceEligibility.cs`

Everything downstream acts on an entity being *missing*, and missing has an innocent explanation for
every guilty one. These guards are the safety story.

**Run-level** — the run was started at the wrong moment; a module that deletes data says so loudly:

| Guard | Prevents |
|---|---|
| `ScanId` is not empty (start-run, before anything is written) | A run with no scan has no ledger; "reconcile everything" could only mean "delete everything". |
| `scan.ingestion_completed_at` is set | A mid-scan sweep reads every source that has not delivered yet as wholly absent. |
| The scan's graph load has not started (start-run **and** every delete-chunk) | Deletions made after the load started would never reach Neptune. Checked per chunk because a redrive resumes mid-run. |

**The empty ledger** is reported, not thrown: start-run warns and hands back an empty wave, and the run
completes having done nothing. It is expected for a scan promoted before the ledger existed; anything
else means promotion never recorded it.

**Per source** — sources come from the scan's configuration (`scan_source_mapping`), not from its tasks,
so a source that was meant to run and produced no task is skipped rather than vacuously passing:

| Skip reason | Why |
|---|---|
| `no_scan_task` | Nothing was delivered for it. |
| `task_not_successful` | A task is not `Completed`. The gate lets a scan finish with a failed task so one bad source cannot stall a tenant; this is what stops that source being swept. |
| `superseded_by_newer_scan` | Another scan has a task for the source created later. It may have seen entities created since, and only the latest scan of a source may sweep it. |

Skipped sources are logged individually and recorded as `skipped` decisions, because *which* source
went unswept is what an operator acts on.

## 5. Deciding — `Reconciliation/AbsencePlanner.cs` (start-run)

Start-run is retried whole, so it first clears the run's staging and decisions. Then per eligible source
and entity type, in this order:

1. **Withdraw** proposals on rows this scan saw again. A proposal is about the latest scan, so a row that
   came back stops being proposed without anyone dismissing it.
2. **Measure** live rows and how many the scan did not see, in one pass. Both sides exclude
   already-deleted rows: tombstones in the denominator only would let the ratio drift upward until a
   healthy source permanently read as over threshold.
3. **Decide**, with integer arithmetic — `absent * 100 > threshold * live` — because the boundary is the
   one place a rounded ratio must not decide whether data is destroyed. Equality is within the threshold;
   a threshold of `0` makes any absence exceed it, which is what "never auto-delete" has to mean.
   - nothing absent → `nothing_absent`;
   - over → `proposed`: stamp `deletion_proposed_at`/`deletion_proposed_scan_id` on absent rows **not
     already proposed**, and change nothing else — not `is_deleted`, not edges, no events. Rows keep their
     *first* proposal, so a source that stays over its threshold costs nothing on later scans;
   - within → `deleted`: append the absent rows to `reconcile_pending` with a contiguous `seq`.

`source.auto_delete_threshold_percent` defaults to **5**, applied per entity type. `0` survives EF only
because of `.HasSentinel((short)-1)` in `TenantDbContext`; with EF's default sentinel an admin's opt-out
would silently become 5.

> **Expect entitlements to trip the threshold.** CyberArk entitlements are synthesized per permission
> someone currently holds, so one disappears when its last holder loses it although the permission still
> exists. On a denominator of a few dozen, two ordinary changes clear 5%. The rule is kept uniform; the
> cost is proposals an admin dismisses. Watch it in the first weeks.

## 6. Deleting — `Reconciliation/EntityDeleter.cs` (delete-chunk)

Waves follow the graph load's shape: a Distributed Map over `THOR_RECONCILE_CHUNKS_PER_WAVE` windows of
`THOR_RECONCILE_ROWS_PER_CHUNK` staged rows, each window `(from, to]` over `seq`. The wave carries the
staged total, so next-wave is pure arithmetic. Staging in Postgres rather than S3: the rows are already
there, a window is an index range scan, and nothing has to be written out or expired.

Each window is **one transaction** — an entity and its edges go together or not at all, so the graph load
never sees a deleted entity whose edges still stand:

1. **The entity rows**, with a `deleted` event each (`scan_manifest_id` NULL, `scan_id` this scan): set
   `is_deleted`, blank `content_hash` to `''`, blank `EdgeRefs` in `raw_attributes`, clear any proposal.
2. **`entity_alias` and `one_sided_ref` rows** of those entities, deleted.
3. **Active `party_assignment` rows** of those entities, deactivated, with a `party_assignment_event`
   each (`entity_deleted`, actor `reconcile_workflow`, the previous owner recorded).
4. **Every live incident edge**, soft-deleted with a `deleted` event each — locked first in id order
   (`ORDER BY id FOR UPDATE`) in the same statement, so parallel windows sharing edges cannot deadlock.

The subtle parts:

- **`content_hash = ''` stops a permanent tombstone.** An entity that comes back usually comes back
  byte-identical, and promotion skips unchanged hashes; ingestion's conflict branch sets
  `is_deleted = FALSE` on the way back in. `''` can never equal a SHA-256 digest.
  `PromoteAssetsAsync_SoftDeletedAssetReExportedUnchanged_IsRevivedViaTheBlankedHash` pins it.
- **Blanking `EdgeRefs` makes that revival worth having.** `EdgeRefDeltaComputer` diffs staged refs
  against canonical's; stale refs would yield no delta, and the revived entity would load with no edges —
  an account that appears to hold nothing.
- **Aliases and parked refs go** so the entity is not resurrected sideways: a leftover alias lets a
  recycled native id resolve someone else's ref onto the tombstone, and `OneSidedRefIndex` clears parked
  refs only for owners in the current seed, which a deleted entity never is.
- **Owners are retired here because Ownership never would.** Ownership runs per manifest and scopes
  each run to the entities that manifest changed; a deleted entity is in no manifest, so its owner would
  stay active on a tombstone — and still count as evidence for `sibling_match`, whose sibling join reads
  `is_active` but not `is_deleted`. Edge-based rules need nothing: they already skip deleted edges. An
  entity that comes back is promoted with a change event, and its manifest's Ownership run finds it
  unassigned and proposes again; a human-confirmed (`is_override`) owner is retired too and is not
  restored on revival — the event keeps the record.
- **Edge events are how PAI stays correct.** PAI's `ChangeSet` reads a scan's edge events with no
  manifest filter, so it re-evaluates exactly the containers that lost members.
- **A scan still ingesting wins.** A row that a scan with `ingestion_completed_at IS NULL` has already
  seen is skipped: that newer scan knows better, and once it has promoted the row nothing would revive it.

Every write is guarded by `is_deleted = FALSE`, so a retried or redriven window changes nothing twice and
emits no second round of events. An edge between two entities deleted in the same run gets one event.

## 7. Reporting — `tenant.reconcile_decision`

One row per `(run, source, entity type)`: `outcome` (`deleted`, `proposed`, `nothing_absent`,
`skipped`), `skip_reason`, `live_count`, `absent_count`, `threshold_percent`, `proposals_withdrawn`,
`decided_at`, indexed by `scan_id` and `run_id`. A run with any `proposed` decision closes its workflow
row as **`completed_with_proposals`** rather than `completed`, so "this scan needs an admin" is one
lookup. The proposed rows themselves are `WHERE deletion_proposed_at IS NOT NULL`.

The API and UI to list, approve and dismiss proposals are follow-up work. Approving needs a path that
deletes outside a scan's run — §10.

## 8. Steps, state machine, idempotency

One image (`--build-arg WORKFLOW=Reconcile`), five steps, all Lambda:

| `THOR_STEP` | Class | Purpose |
|---|---|---|
| `start-run` | `ReconcileStartRunStep` | Guards, open the row, decide, stage, hand back wave 0 |
| `delete-chunk` | `ReconcileDeleteChunkStep` | The Map body: soft-delete one window |
| `next-wave` | `ReconcileNextWaveStep` | Next window set from the carried total, or end the loop |
| `finalize` | `ReconcileFinalizeStep` | Drop the scan's ledger and the run's staging; close the row |
| `record-failure` | `ReconcileRecordFailureStep` | Close the row failed, from the Catch path |

- **One run per scan**: the gate starts it as `reconcile-{scanId}`; later starts are rejected with
  `ExecutionAlreadyExists`. The row's `RunId` is derived from `(TenantId, ScanId)` with this module's own
  namespace (`ReconcileSettings`) — never shared with another module, because `GetByRunIdAsync` is not
  filtered by workflow type.
- **No chunk failure is tolerated**: a window left undeleted would leave its rows in the graph. The Map
  item retries three times, then the run fails; deleted windows stay deleted.
- **Redrive** works until the graph load starts, and is refused after (§4).

## 9. Configuration

| Setting | Where | Default |
|---|---|---|
| `THOR_RECONCILE_ROWS_PER_CHUNK` | `settings.rows_per_chunk` | 5000 |
| `THOR_RECONCILE_CHUNKS_PER_WAVE` | `settings.chunks_per_wave` | 4 — keep equal to `map_max_concurrency` |
| `map_max_concurrency` (ASL) | `settings.map_max_concurrency` | 4 |
| `source.auto_delete_threshold_percent` | per source, tenant DB | 5 |

Under `var.workflows["reconcile"]` in each environment's `terragrunt.hcl`; `enabled = false` everywhere
until a first deployment (`docs/Infra_pipeline/workflow-deployment.md`, "First deployment of reconcile").

## 10. Known gaps

- **The ledger grows while reconcile is off or failing.** Ingestion writes it for every scan regardless;
  only reconcile's finalize drops it. With reconcile disabled — the default — nothing does.
- **A failed or abandoned run leaves its ledger and staging behind.** Same cause; a cleanup is wanted.
- **Full-dump assumption.** A delta-style connector must be excluded before it ships.
- **Not every consumer filters `is_deleted`.** The graph load drops the vertex, PAI writes no route for
  a deleted account and prunes the ones it had (`RouteWriter`, `PaiPruner`), and Ownership's owners are
  retired by the deletion itself (§6). ATRE does not filter — it runs per manifest, and an absent
  account is in no manifest, so its last classification simply lingers — and Thor.Api returns
  `is_deleted` as a field without filtering lists on it.
- **Proposed rows stay fully live.** Over the threshold nothing is deleted, so the graph keeps the
  vertices and PAI keeps their routes until an admin acts. Deliberate: a mass absence is more likely a
  partial export than a mass departure, and hiding access that may still exist is the failure the
  threshold exists to prevent.
- **No approve/dismiss path.** Proposals accumulate until the follow-up API exists. **Requirement:** an
  admin's approval must run reconcile and everything downstream of it again — the deletion, the graph
  load that drops the vertices, then PAI — not wait for the next scan. Today's shape does not allow it:
  execution names are pinned per scan (`reconcile-`/`graph-load-`/`pai-{scanId}`), reconcile refuses a
  scan whose graph load has started, and the graph load selects its changes by scan id. An approval
  therefore needs its own run identity: delete the approved rows (the approval is the evidence, so no
  ledger), load just those changes, then start PAI. Ownership needs no re-run — the deletion retires
  its owners (§6). Dismissal needs a decision too: whether a dismissed row stays unproposed until it is
  seen again, or is proposed afresh by the next scan.
- **The gate waits on an unclosed row.** An execution stopped by hand, timed out, or whose
  RecordFailure fails leaves its row open, and the scan's graph load waits (`scan_completion_gate.md` §5).
- **Volumes are unmeasured.** The 5% threshold bounds an ordinary scan's deletions but not their edges;
  chunk sizes are guesses, and the graph load drops vertices in one invocation.
- **Existing tenants need the schema first** — and so do ingestion and the graph load built from this
  change, whether or not reconcile is enabled.

## 11. Key files

| Concern | File |
|---|---|
| Guards | `Scoping/SourceEligibility.cs` |
| Decisions, staging, proposals | `Reconciliation/AbsencePlanner.cs` |
| Deletion | `Reconciliation/EntityDeleter.cs` |
| Steps, waves, dials | `Steps/Reconcile*Step.cs`, `ReconcileWavePlanner.cs`, `ReconcileSettings.cs` |
| Ledger write | `backend/workflows/ingestion/Thor.Workflows.Ingestion/Promotion/Promoter.cs` |
| Schema | `backend/shared/Thor.DataLayer/Models/Tenants/` (`ScanSeenEntity`, `ReconcilePending`, `ReconcileDecision`) |
| Graph side | `backend/workflows/graph-load/Thor.Workflows.GraphLoad/Persistence/GraphLoadWriteSet.cs` |
| Gate | `backend/functions/Thor.ScanGate/` (`IReconcileStarter`, `ScanGateEvaluator`) |
| State machine | `infra/src/modules/workflow/asl/reconcile.asl.json` |
| Terraform | `infra/src/workflow_definitions.tf` (`reconcile`), `infra/src/workflows.tf` (`scan_gate`) |
| Tests | `backend/workflows/reconcile/Thor.Workflows.Reconcile.Tests/` (Testcontainers Postgres) |
