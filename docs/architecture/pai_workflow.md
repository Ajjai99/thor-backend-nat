# PAI Workflow

> Covers `backend/workflows/pai/Thor.Workflows.Pai/`, the graph read side in
> `backend/shared/Thor.Graph/GraphReader.cs`, and the state machine in
> `infra/src/modules/workflow/asl/pai.asl.json`. What starts it is in
> [scan_completion_gate.md](scan_completion_gate.md).

## 1. What PAI does

PAI (Privileged Access Instances) finds every **route** by which an account reaches a privileged
target, and writes one `pai_result` row per route. A privileged target is a group or asset that
matches a `privilege_definition`: for example, AD's well-known admin groups, the local Administrators
group, or a CyberArk safe. An account nested three groups deep under Domain Admins by two different
chains has two routes, and each is kept, together with the path it takes.

The definitions ship as config (`Privilege/Config/*.json`, one file per connector). Each run seeds
them into `privilege_definition` and deactivates any that are no longer shipped. A definition matches
only targets from its own connector: without that, AD's "Administrators" would match every Windows
host's local Administrators group, and the reverse.

PAI runs **once per scan**, started by the scan-completion gate once the scan's graph load has
succeeded and ATRE and Ownership are done. It reads the graph graph-load wrote, through Neptune's
**reader endpoint**, and never writes to it. Reads are bytecode, like every Thor.Graph reader, because
Neptune does not support script bindings.

## 2. The run

```
Envelope → StartRun → Expand ⟲ → FanoutWave (Distributed Map) → NextWave ⟲ → Finalize
any Task Catch → RecordFailure;   inside the Map: Catch → RecordChunkFailure → tolerated
```

| Step | What it does |
|---|---|
| `start-run` | Opens the `workflow` row (`workflow_type = pai`, `scan_id` set, run id derived from the scan). Seeds definitions and stages what this run recomputes (§3). Returns wave 0. |
| `expand` | Stages every simple path **down the group layer** from each computed target, one level per round (§4). Runs on a time budget and resumes from the deepest level it finished. |
| `fanout` | One Map item per window of staged paths. Fetches the direct account members of each path's last node and upserts a route per (account, path). |
| `next-wave` | Sums the paths the wave read. A full wave is followed by another; a short one ends the loop. |
| `finalize` | Prunes (§5), closes the row `completed` or `completed_with_errors`, and drops the run's staging. |
| `record-failure` / `record-chunk-failure` | The Catch paths, shaped like ATRE's and Ownership's. A failed chunk also marks its paths `failed`. |

Every Neptune query is **bounded**: either a label match, or a single hop from a batch of up to 200
vertices. A deeper traversal is a loop of these. A single variable-length traversal down to accounts
is what runs into Neptune's query timeout, so nothing here issues one.

## 3. What a run recomputes (`Seeding/`)

Each definition is evaluated in one of two modes. The mode is fixed at start in `pai_run_definition`:

- **Full**, when `privilege_definition.definition_hash` differs from `computed_hash`. That covers a new
  definition, an edited one, and one that has never completed. Every matching target is seeded by a
  label match. A config edit produces no change event, which is why an edited definition is
  recomputed in full.
- **Incremental** otherwise. Seeds come from the change window: the `ingest_change_event` rows of
  every scan that no completed run has covered. Each run records its window in `pai_run_scan`, and a
  scan counts as covered once a run with status `completed` has read it. A failed or partial run's
  scans therefore roll into the next run. A scan still ingesting (no `analysis_completed_at`) waits
  for a later run.

An incremental definition has three seed sources, because each one catches changes the others miss:

1. **Changed edges of the definition's relationship.** The container (the edge's `to` side) is walked
   *up* `MEMBER_OF`, a hop per level up to the depth cap, and every group reached is tested against
   the pattern. This is necessary: adding a member to Helpdesk, which is nested under Domain Admins,
   changes an edge whose container matches nothing. Only its ancestor is a usable seed.
2. **Changed targets.** A target that matches now is recomputed, which covers renames into the
   pattern. A target that no longer matches but still has routes is **retired**, and all its routes
   are removed.
3. **Routes through a changed group.** Once a group has been deleted from the graph, the walk up can't
   pass through it. The routes already materialised through it (`pai_result.via_node_ids`,
   GIN-indexed) name the targets to recompute.

The pipeline tests remove each of these in turn to confirm the suite notices (§7).

## 4. Expansion (`Expansion/GroupPathExpander.cs`)

Each computed target gets a depth-0 path, which is the target alone. Each round then:

1. takes the paths at the deepest staged depth;
2. fetches the child groups of their last nodes in one batched hop (`in('MEMBER_OF').hasLabel('grp')`);
3. extends each path by every child not already on it, which keeps paths simple and cycles finite.

A level is inserted in one transaction, so the deepest depth is always a finished level. There are two
limits, and both log a warning when hit:

- the **depth cap** (10), the same cap the walk up uses;
- the **per-target path cap** (`THOR_PAI_MAX_PATHS_PER_TARGET`), a guard against diamond lattices.

Only groups are expanded. Accounts are the fan-out's final hop, so the expensive work is proportional
to the group layer, not the account layer. Single-hop definitions such as CyberArk's `HAS_ACCESS`
stay at depth 0.

## 5. Writing and pruning

The fan-out **upserts**. It never deletes first, so readers never see a target with no routes while a
run is in flight. A route that is proven again gets this run's id and a fresh `computed_at`, set with
database time.

Finalize removes the routes the run is responsible for that it did not prove again, meaning
`computed_at` is older than the run's start:

- for a full definition, all of its routes;
- for an incremental definition, only the routes of its computed seeds;
- for a retired seed, all of its routes;
- routes of deleted accounts, and of deactivated definitions.

**A target with a path in a failed chunk is never pruned.** Its routes were only partly rewritten, so
stale routes are kept, which errs toward over-reporting. The run closes `completed_with_errors`,
which covers no scans and advances no hashes, so the next run redoes the same work.

Pruning compares **times**, not run ids, so overlapping runs are safe. Two scans can finish close
together and run concurrently. A later run's rows are never older than an earlier run's start, so
neither run deletes the other's fresh routes. The worst case is a stale route surviving until the
next run touches its target.

Prune, hash advance and the status update commit in one transaction.

## 6. Configuration

In `infra/src/workflow_definitions.tf`, overridable per environment through `var.workflows["pai"].settings`:

| Setting | Default | Meaning |
|---|---|---|
| `paths_per_chunk` | 500 | Staged paths per Map item. |
| `chunks_per_wave` | 5 | Items per Map. Keep equal to `map_max_concurrency`. |
| `map_max_concurrency` | 5 | Concurrent fan-out invocations. Each holds two DB connections. |
| `expand_budget_seconds` | 600 | Per-invocation budget for expand. |
| `max_paths_per_target` | 10000 | Per-target path cap. |

**None of the defaults has been measured.** A path's cost is its last group's member count, which
varies by orders of magnitude.

## 7. Tests (`Thor.Workflows.Pai.Tests`)

- **Pipeline** (Postgres via Testcontainers, plus an in-memory graph). The run is driven the way the
  state machine drives it: a zero expand budget to exercise resume, and small chunks to exercise
  several waves. Every result is compared with a brute-force enumeration of all simple paths. Cases:
  - nesting, diamonds and cycles;
  - connector scoping;
  - single hop;
  - **incremental equals full** over three random sequences of edge adds and removes, renames, group
    and account deletions, with no full label scan after the first run;
  - a run with no changes rewrites nothing;
  - a failed chunk keeps its target's routes, and the next run fixes them;
  - an edited definition recomputes in full;
  - overlapping runs.
- **Graph reader** (a real `tinkerpop/gremlin-server`, seeded through `GraphBatchWriter`, so the
  reader is tested against what graph-load writes): label and connector scoping, operators, one-hop
  reads in each direction, per-label batching, seed batching, tenant isolation on both ends of a hop,
  and every request sent as bytecode without bindings.
- **Checked against deliberate breakage:** with seed source 1 or 3 disabled, or with pruning by run
  id, the suite fails, and it fails if the reader's far-side tenant filter is removed.

## 8. Open risks

- **Entity deletion is not detected on dev.** Normalizers never set `is_deleted`, so departed accounts
  remain in both stores, and therefore in PAI.
- **Very large groups.** Fetching the direct members of a group with ~10⁵ members is one unmeasured
  Neptune response. If it is too slow, the fix is to page that final hop.
- **Existing tenants' schema.** The new tables and columns reach new tenants through the regenerated
  migration. Existing tenants need Blue/Green, as with every tenant-schema change.

## 9. Key file reference

| Concern | File |
|---|---|
| Steps | `Steps/Pai*Step.cs` |
| Seeding, change window | `Seeding/PaiSeedPlanner.cs`, `Seeding/ChangeSet.cs` |
| Group-layer expansion | `Expansion/GroupPathExpander.cs` |
| Routes | `Fanout/RouteWriter.cs`, `Fanout/AccessPath.cs` |
| Prune | `Persistence/PaiPruner.cs` |
| Definitions | `Privilege/`, `Privilege/Config/*.json` |
| Graph reads | `backend/shared/Thor.Graph/GraphReader.cs`, `TenantTraversal.cs`, `PrivilegePattern.cs` |
| Schema | `backend/shared/Thor.DataLayer/Models/Tenants/Pai*.cs`, `PrivilegeDefinition.cs` |
| Infrastructure | `infra/src/workflow_definitions.tf` (`pai`), `infra/src/modules/workflow/asl/pai.asl.json` |
