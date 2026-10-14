# Deploying a workflow module

How a workflow module (ingestion, and the ATRE/PAI/Reconcile modules to come) gets built, deployed
and first stood up. Companion to [infra-pipeline.md](infra-pipeline.md), which covers the three ECS
services.

## The short version

**A workflow image deploy does not touch Terraform.** Terraform owns the shape of the compute — the
state machine, the task definitions, the functions, the IAM — and points all of it at a constant
floating tag. CI moves that tag. Nothing about deploying a new build is an infrastructure change.

```
CI:  docker build --build-arg WORKFLOW=Ingestion -f backend/workflows/Dockerfile .
     push JFrog        workflow-<name>:<sha>        <- source of truth for promotion
     push ECR          thor-<env>-<name>-ecr:<sha>  <- immutable, never moves
     put-image         thor-<env>-<name>-ecr:deployed  -> same manifest, no layers transferred
     update-function-code on each image-packaged step Lambda
     write deploy/workflow-<name>/image.json
```

## Where the state machine is defined

`infra/src/modules/workflow/asl/<name>.asl.json` — plain Amazon States Language, committed, one file
per workflow. Terraform reads it with `templatefile` and substitutes the `${...}` placeholders; it
owns the ARNs and nothing else about the graph.

```
asl/ingestion.asl.json          the definition, as Step Functions runs it
        |  templatefile(local.asl_path, local.asl_vars)
        v
aws_sfn_state_machine.workflow
```

This module used to build the definition out of HCL in `locals.tf`. That could not be checked by
anything — no ASL tooling reads Terraform locals — so the first validation was AWS rejecting the
apply, which is how two ItemProcessors shipped with colliding state names.

**Editing it:**

- `var.steps` in `workflow_definitions.tf` says which compute each `THOR_STEP` needs. The ASL says
  how those are wired into states. Adding a step usually touches both.
- `templatefile` fails the plan if the definition names a `$${lambda_arn_x}` that `var.steps` does not
  produce, or vice versa, so the two cannot silently diverge.
- Validate before pushing: `./scripts/validate-asl.sh`. CI runs the same script in
  `infra.yml`'s validate job, using `aws stepfunctions validate-state-machine-definition`.
- Retry counts, backoff rates and wait intervals are literals in the definition, not Terraform
  variables — they are orchestration decisions and belong with the graph.
- Numbers that are *configuration* rather than graph shape go through `definition_vars`, which adds
  `$${...}` substitutions on top of the ARNs the module supplies. A Distributed Map's
  `MaxConcurrency` is the case this exists for: it states how much Aurora capacity an environment
  has, not how the workflow is shaped. ATRE reads it from
  `var.workflows["atre"].settings.map_max_concurrency` (default `"5"`). Values substitute as plain
  text, so a placeholder used where JSON wants a number must be written **unquoted** in the
  definition. `definition_vars` may not redefine a substitution the module owns, nor use the
  `lambda_arn_*` / `task_definition_arn_*` / `state_machine_arn_*` prefixes.
- To open one in Workflow Studio, substitute the placeholders first; `scripts/validate-asl.sh`
  renders exactly that into `.asl-validate/`.

## Chaining one workflow to another

A workflow that starts another declares it once, as `chained_workflows` on its entry in
`workflow_definitions.tf`, and writes the state itself in its ASL. Ingestion → ATRE is the worked
example:

```hcl
# workflow_definitions.tf
ingestion = {
  ...
  chained_workflows = ["atre"]
}
```

```json
// asl/ingestion.asl.json
"StartAtre": {
  "Type": "Task",
  "Resource": "arn:aws:states:::states:startExecution",
  "Parameters": {
    "StateMachineArn": "${state_machine_arn_atre}",
    "Input": { "TenantId.$": "$.Request.TenantId", "ScanManifestId.$": "$.Request.ScanManifestId" }
  },
  ...
}
```

Naming it there is what produces both the `${state_machine_arn_<name>}` placeholder and a
`states:StartExecution` grant scoped to exactly that one ARN — nothing broader, and no
`DescribeExecution`.

Three things this arrangement decides, worth knowing before you add the next one:

- **The target ARN is a built string, not a module output.** Same rule the DLQ uses for the trigger
  queue and Neptune uses for the graph-load bucket. That is what keeps a chain out of the Terraform
  dependency graph, so `A → B` and `B → A` are both expressible without a cycle. The cost: nothing
  in the module notices a target this environment does not host. The `check` block in `workflows.tf`
  is what covers that, and it warns rather than failing — an environment mid-rollout is a legitimate
  state.
- **It is fire and forget.** The optimized (non-`.sync`) integration returns as soon as the child
  execution starts. The parent cannot see whether the child succeeded, by design: each workflow owns
  its own failure handling and its own DLQ. A must-wait case would need `.sync`, which is not built.
- **Project the `Input` field by field.** Passing `$.Request` wholesale forwards the parent's
  `RunId`, and `WorkflowLifecycle` looks a workflow row up by `RunId` alone when it has one — so the
  child would find and overwrite the parent's row in the tenant `workflow` table.

Because the child's execution has no pinned `Name`, a parent retry starts it twice. That is the
child's problem to be idempotent about, which is the same bar every step already has to meet.

## Why ECS needs nothing and Lambda does

The two compute targets run the same image and differ only in when they resolve the tag.

| | Resolves the tag | What a deploy has to do |
|---|---|---|
| ECS Fargate | at task launch | Nothing. The next `RunTask` pulls the new image. No new task definition revision. |
| Lambda | at update time | `update-function-code` against the same floating tag. |

That ECS property is what lets the state machine keep pinning a revision-qualified task definition
ARN. The revision never has to change, so the original defect — CI registering revisions the state
machine could never reach — cannot recur.

For Lambda, CI passes the **floating tag**, not the `:<sha>` tag. That keeps the function's stored
`ImageUri` identical to what Terraform configured, so no drift shows up on the next plan.

## Why a floating tag and not `ignore_changes = [image_uri]`

`ignore_changes` looks equivalent and is not. With it, the next unrelated Terraform apply re-registers
a task definition carrying whatever image is written in config — the bootstrap one — and since ECS
takes the latest revision, that **silently rolls the image back**. A tag that always resolves to the
current build removes the failure mode rather than working around it.

The repository is `IMMUTABLE_WITH_EXCLUSION` with a wildcard filter on exactly this tag: every
`:<sha>` stays pinned to its digest, and the one tag CI moves is the only mutable thing in it.

## What Terraform no longer knows

Which build is running. `terraform plan` shows clean whether prod is on last week's image or today's.

`deploy/workflow-<name>/image.json` is the record — the same arrangement the three ECS services
already use, so there is one model here, not two. If you need to know what is deployed, read the
manifest or ask ECR which digest `:deployed` points at.

## First deployment of a workflow

The order matters, because Lambda validates `image_uri` at CreateFunction. There is no image on a new
environment, so the compute cannot be created first.

1. **Apply with the workflow listed but disabled.** In `infra/envs/<env>/terragrunt.hcl`:

   ```hcl
   workflows = {
     ingestion = { enabled = false }
   }
   ```

   This creates the ECR repository, the security group and the shared cluster — and nothing else.
   The security group exists regardless of `enabled` because `rds_proxy` and `neptune` build their
   ingress rules from it.

   **Check the plan does not create an S3 bucket named `thor-<env>-ingestion-<account>`.** That is
   the live uploads bucket, owned by `modules/uploads`. If a plan proposes creating it, the trigger
   module's `bucket_id`/`bucket_arn` wiring is wrong — stop rather than letting the apply 409.

2. **Let CI push an image.** Any push to `dev` touching `backend/workflows/**` or `backend/shared/**`
   builds and pushes it. The deploy will report no functions to update, which is correct and
   expected at this point:

   ```
   No image-packaged functions named thor-dev-workflow-ingestion-* — this workflow is not enabled yet.
   ```

3. **Flip `enabled = true` and apply.** The step Lambdas are created against `:deployed`, which now
   resolves to the build from step 2. This is the first time the workflow has ever run.

   If this workflow is named in `neptune_bulk_load_workflows` and is the first of them enabled in
   the environment, this apply also creates the Neptune bulk-load role and attaches it to the
   cluster, and takes a **60-second pause** while doing so. That wait is
   `time_sleep.bulk_load_role_propagation` in `modules/neptune`; see below for why. Enabling a later
   one only widens the role's policy to that workflow's graph-load bucket — the role is already
   attached, so there is no re-attach and no pause.

From here, deploys are step 2 alone. Steps 1 and 3 happen once per environment.

### Why the bulk-load role has a sleep in front of it

Attaching it used to fail:

```
InvalidParameterValue: IAM role ARN value is invalid or does not include
the required permissions for: AWS_ROLE_INTEGRATION
```

The wording points at permissions, and that is misleading — every permissions explanation was checked
against dev and eliminated:

| Suspected cause | How it was ruled out |
|---|---|
| Inline policy not yet written when the attach ran | `get-role-policy` returned it on the live role |
| Permissions boundary too narrow | `simulate-principal-policy` on the live role: both S3 actions `allowed` |
| Graph-load bucket did not exist | attach failed identically with the bucket present |
| Bucket needs `kms:Decrypt` | bucket is AES256, no CMK |
| Wrong trust principal | `rds.amazonaws.com`, as Neptune documents |
| Deploy role lacks `iam:PassRole` / `rds:AddRoleToDBCluster` | both `allowed`, simulated with `iam:PassedToService` supplied |

What was left is that **IAM is eventually consistent and `AddRoleToDBCluster` validates the role as it
attaches**. A role created seconds earlier is rejected however correct it is; re-running the same
apply minutes later succeeds with no change at all. That is the observed behaviour here and the
documented behaviour elsewhere — it is a first-run failure, not a configuration error.

An earlier revision of this document blamed bucket absence and prescribed a two-apply split
(`neptune_bulk_load_workflow = ""`, then restore). That was wrong twice over: the bucket was not the
cause, and the split does not address propagation either, because the apply that restores the name
still creates the role and attaches it in one pass. It is removed.

The wait is a sleep because there is nothing better to wait on. The attach is an argument on
`aws_neptune_cluster`, not a resource — the provider ships `aws_rds_cluster_role_association` but no
`aws_neptune_cluster_role_association` — so a failed attach fails the cluster itself and halts the
rest of the apply. Only the first apply in an environment pays the 60 seconds; `time_sleep` persists
in state.

Still worth doing separately: move the graph-load bucket out of `module.workflow` into a platform
module keyed by workflow name, the shape `workflow_network` already uses. That gives `module.neptune`
a real reference to the bucket instead of a constructed ARN string. It is a correctness improvement
on its own and is unrelated to this failure.

### First deployment of graph-load, which the scan-completion gate starts

graph-load is all Lambda, so it follows the three steps above. Two things are different:

- **The scan-completion gate exists only while graph-load is enabled** (`workflows.tf`,
  `module.scan_gate`; and Thor.TaskApi's `THOR_SCAN_GATE_QUEUE_URL` with it). Ingestion stopped
  writing the graph in the same change, so between deploying that ingestion and step 3 here no scan's
  graph is written at all. Keep that window short, or deploy when no scans are running.
- **Deploy order.** The new ingestion image and the new ingestion definition (`ResolveEdges` replacing
  `GraphStage`) must go out together — an ingestion execution that runs between the two references a
  step its image or definition does not have. Deploy when no scans are running.

A scan that was mid-ingestion across the change is graph-loaded normally: graph-load reads Postgres,
not anything the old steps staged.

### First deployment of Ownership, which ingestion chains to

Ownership follows the same three steps, with one ordering constraint: ingestion's definition starts
it (`StartOwnership`, after `StartAtre`), and the chain's target is a built ARN, so from the moment
ingestion's definition carries that state, every ingestion execution tries to start
`thor-<env>-workflow-ownership-sf`. While Ownership is still `enabled = false` there is no such state
machine — the `check "chained_workflows_are_hosted_here"` warning says so at plan time — and each
ingestion execution fails its last state, catches to the DLQ and ends failed, although its own work
committed.

So in an environment where ingestion is enabled, keep the window between steps 1 and 3 short, or land
the chaining change (`StartOwnership` in `asl/ingestion.asl.json` and `"ownership"` in ingestion's
`chained_workflows`) only with the apply that sets `ownership = { enabled = true }`. In an environment
where ingestion is itself disabled there is nothing to order.

`neptune_bulk_load_workflows` still lists `ownership` in every environment, only until Ownership's
retired graph-load bucket has emptied (`ownership_workflow.md` §7); it grants nothing until Ownership
is enabled.

### First deployment of PAI

PAI follows the same three steps. Nothing chains to it. Its only trigger is the scan-completion gate,
which already exists wherever graph-load is enabled. The step-3 apply gives the gate
`THOR_PAI_STATE_MACHINE_ARN` and `states:StartExecution` on PAI's state machine; until then the gate
only starts graph loads. Check that `thor-<env>-role-boundary` allows that grant before step 3.

### First deployment of reconcile

Reconcile follows the same three steps, and like PAI its only trigger is the scan-completion gate. The
step-3 apply gives the gate `THOR_RECONCILE_STATE_MACHINE_ARN`, `states:StartExecution` on reconcile's
state machine and an EventBridge match on its status changes; until then the gate goes straight to the
graph load and nothing absent from a scan is deleted. Check that `thor-<env>-role-boundary` allows the
grant before step 3.

**The schema goes first, independently of `enabled`.** The same change makes ingestion record every
promoted row in `tenant.scan_seen_entity`, and the graph load read `asset.is_deleted` — so the ingestion
and graph-load images built from it fail on a tenant database without the new tables and columns
(`scan_seen_entity`, `reconcile_pending`, `reconcile_decision`, the proposal columns, `asset`/`entitlement`
`is_deleted`, `source.auto_delete_threshold_percent`, a nullable `ingest_change_event.scan_manifest_id`).
New tenants get them from the regenerated migration; existing tenants need Blue/Green before those
images roll, as with every tenant-schema change.

A scan in flight when reconcile is enabled is safe: one whose graph load already started is refused by
reconcile's start-run, which closes the row failed, and the gate carries on.

## Verifying a first deployment

- Drop a fixture export under the notification's prefix in the uploads bucket —
  `tenants/<tenantId>/uploads/...`, matching what `Thor.TaskApi` presigns
  ([UploadService.cs](../../backend/services/Thor.TaskAPI/Services/UploadService.cs)). A Step
  Functions execution should start and run `SelectCompute` → `ListFiles` → `ExtractStage` →
  `Promote` → `ResolveEdges` → `StartAtre` → `StartOwnership` → `WorkflowSucceeded`.
- Once the scan's tasks report done, confirm the scan-completion gate starts one
  `graph-load-<scanId>` execution of the graph-load state machine, which runs `StartRun` →
  `WriteWave`/`NextWave` (vertex waves, then edge waves) → `Finalize`, closes the scan's `graph-load`
  workflow row `completed`, and that Neptune's vertex and edge counts for the tenant match Postgres.
  Its plan and id files land in its own `graphload` bucket, never the uploads bucket, so they cannot
  re-trigger ingestion.
- Where PAI is enabled, confirm `pai-<scanId>` starts only after both that graph load and the last
  ATRE/Ownership run of the scan have finished, whichever ends last.
- Force `compute = "choose"` down the ECS branch on one step and confirm the execution shape is
  identical — both branches reconverge, so nothing downstream can tell which target ran.
- Force a step failure and confirm the DLQ message names the failed state and compute target.
- Ingestion's own `graphload` bucket has no writer any more. Neptune's loader role still reads it —
  kept as the fallback for very large initial scans until the graph load is benchmarked.
- For Ownership: after an ingestion run, confirm an Ownership execution starts, runs
  `StartRun` → `Walk` → `VoteWave`/`NextWave` through all three phases → `Finalize`, that its
  `workflow` row closes `completed` under the ingestion row in `workflow_graph`, and that
  `party_assignment` has rows for its run id. It writes nothing to Neptune: its winners are
  proposals (`ownership_workflow.md` §7).

## Rollback

`workflow_dispatch` on Deploy with an explicit `image_digest` promotes that digest instead of reading
the manifest. It moves the deploy tag and updates the functions exactly as a normal deploy does.

There is no Terraform apply involved in a rollback, which is the point: the thing you are changing is
which build the tag points at, and that was never Terraform's to own.
