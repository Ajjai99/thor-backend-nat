# What each workflow is: its steps, its buckets, what its code reads, and how it is triggered.
# Adding ATRE, PAI or Reconcile is an entry here plus a name in each environment's var.workflows —
# no new module, and no state machine written by hand.

locals {
  workflow_rds_db_arn_prefix = "arn:aws:rds-db:${var.aws_region}:${var.account_id}:dbuser:${module.rds_proxy.proxy_resource_id}"

  # Two dbusers: the Master-DB read role and the per-tenant roles (tenant_* covers _rw and _ro).
  # Scoped by proxy resource ID — a cluster-scoped ARN authorizes nothing on this leg.
  workflow_rds_db_connect_resources = [
    "${local.workflow_rds_db_arn_prefix}/${var.master_db_app_user}",
    "${local.workflow_rds_db_arn_prefix}/tenant_*",
  ]

  # Granted to every workflow compute role, ECS task and Lambda alike, so the two targets cannot
  # drift in what the same step code may do. Neptune only when it is on: an empty cluster resource id
  # is not a valid neptune-db ARN.
  workflow_compute_policy_statements = concat(
    [
      {
        actions   = ["s3:GetObject", "s3:ListBucket"]
        resources = [module.uploads.bucket_arn, "${module.uploads.bucket_arn}/*"]
      },
      {
        actions   = ["rds-db:connect"]
        resources = local.workflow_rds_db_connect_resources
      },
      # Broad read on every thor-<environment>-* secret (AD/CyberArk/Windows connector credentials) —
      # the step picks which one it needs at runtime, so it cannot be scoped to a fixed ARN. On the
      # compute role, not an execution role: this is the container's own SDK call.
      {
        actions   = ["secretsmanager:GetSecretValue"]
        resources = ["arn:aws:secretsmanager:${var.aws_region}:${var.account_id}:secret:thor-${var.environment}-*"]
      },
    ],
    # DeleteDataViaQuery is what Neptune checks for drop(), which the graph load's deletes use;
    # Read/Write cover the mergeV/mergeE upserts. The loader actions stay while the bulk loader is
    # kept as the fallback for very large initial scans.
    var.enable_neptune ? [{
      actions = [
        "neptune-db:StartLoaderJob",
        "neptune-db:GetLoaderJobStatus",
        "neptune-db:CancelLoaderJob",
        "neptune-db:ReadDataViaQuery",
        "neptune-db:WriteDataViaQuery",
        "neptune-db:DeleteDataViaQuery",
        "neptune-db:GetQueryStatus",
      ]
      resources = ["arn:aws:neptune-db:${var.aws_region}:${var.account_id}:${module.neptune[0].cluster_resource_id}/*"]
    }] : [],
  )

  # Read by TenantConnectionManagerFactory. Identical for both compute targets: neither uses password
  # auth, both mint an RDS IAM token for THOR_MASTERDB_USER. THOR_AWS_REGION is explicit because
  # Fargate, unlike Lambda, never sets AWS_REGION.
  workflow_common_environment = {
    THOR_MASTERDB_HOST     = module.rds_proxy.endpoint
    THOR_MASTERDB_DATABASE = module.aurora.database_name
    THOR_MASTERDB_USER     = var.master_db_app_user
    THOR_MASTERDB_REGION   = var.aws_region
    THOR_MASTERDB_PORT     = "5432"
    THOR_MASTERDB_USESSL   = "true"
    THOR_AWS_REGION        = var.aws_region
  }

  workflow_definitions = {
    ingestion = {
      # One entry per THOR_STEP: which compute it needs, and under which role. How these are wired
      # into states — extract-stage running twice, the Choice on $.Compute.Target, the Map fan-out,
      # the poll loop — is in modules/workflow/asl/ingestion.asl.json, which is what Step Functions
      # runs and what you read when debugging an execution.
      #
      # "both" means the definition can send the step to either target at run time. It does not
      # decide; select-compute does, by measuring the manifest.
      steps = {
        "select-compute" = { compute = "lambda" }

        # ecs:runTask.sync has no return-value channel, so the list-mode state that hands back the
        # file list must be the Lambda half. The Map body may be either.
        "extract-stage" = { compute = "both" }

        "promote" = { compute = "both" }

        # A manifest's last step: resolves its edges into Postgres. It writes nothing to Neptune —
        # the graph-load workflow below does that once per scan.
        "resolve-edges" = { compute = "both" }
      }

      # Where the bulk-load CSVs went when ingestion still wrote the graph itself. Nothing writes it
      # now. Kept only because Terraform cannot delete a bucket that still holds objects, and because
      # Neptune's bulk-load role still reads it (main.tf, neptune_bulk_load_workflows) — the fallback
      # for very large initial scans until the graph load is benchmarked on dev. Remove both once its
      # 14-day lifecycle has emptied it and the fallback is no longer wanted.
      buckets = {
        graphload = {
          retention_days = 14
          write_roles    = ["graph-load"]
          env_var        = "THOR_GRAPH_BULKLOAD_BUCKET"
        }
      }

      environment_variables = merge(local.workflow_common_environment, {
        # Read by SizeThresholdPolicy.FromEnvironment() in the select-compute step.
        THOR_COMPUTE_LAMBDA_MAX_BYTES = "5242880"
      })

      trigger = {
        # The only literal prefix that matches every upload: Thor.TaskApi presigns
        # tenants/{tenantId}/uploads/{scanId}/{sourceId}/{file} (Services/UploadService.cs), tenantId
        # varies, and S3 notification filters take no wildcards. It does not match graph-bulk-load/...,
        # which is what the pipeline itself writes — that overlap was the feedback loop.
        filter_prefix = "tenants/"
        source_dir    = var.create_manifest_source_dir
        entrypoint    = "Thor.CreateManifest.Function::Thor.CreateManifest.Function.Function::FunctionHandler"
        # Thor.CreateManifest/CompositionRoot.cs's existing contract. Renaming it belongs with folding
        # the handler into the workflow image, not here.
        state_machine_env_var = "THOR_INGESTION_STATE_MACHINE_ARN"
        environment = {
          THOR_MASTERDB_HOST     = module.rds_proxy.endpoint
          THOR_MASTERDB_DATABASE = module.aurora.database_name
          THOR_MASTERDB_USER     = var.master_db_app_user
          THOR_MASTERDB_REGION   = var.aws_region
          THOR_MASTERDB_PORT     = "5432"
          THOR_MASTERDB_USESSL   = "true"
        }
        policy_statements = [{
          actions   = ["rds-db:connect"]
          resources = local.workflow_rds_db_connect_resources
        }]
      }

      definition_vars = {}

      # Ingestion's last states start ATRE and then Ownership, each returning immediately — see
      # StartAtre and StartOwnership in asl/ingestion.asl.json. Naming them here is what produces the
      # ${state_machine_arn_atre} / ${state_machine_arn_ownership} placeholders those states
      # substitute, and the states:StartExecution grant behind each.
      chained_workflows = ["atre", "ownership"]

      # Fargate CPU units and memory (MiB) for the ECS half of the "both" steps, tunable per
      # environment via settings. Must be a valid Fargate pair (1024 CPU allows 2048-8192 MiB, 2048
      # CPU allows 4096-16384 MiB, both in 1024 steps).
      task_cpu    = try(var.workflows["ingestion"].settings.task_cpu, "1024")
      task_memory = try(var.workflows["ingestion"].settings.task_memory, "4096")
    }

    atre = {
      # All Lambda. Classification is per account and reads no other account's result, so the work
      # parallelizes cleanly: a Distributed Map runs a wave of chunks, each chunk reading its own
      # window of the accounts in scope. See asl/atre.asl.json.
      steps = {
        # Runs once: opens the run's workflow row and hands back the first wave. Must be lambda —
        # the state machine reads that wave off its response.
        "start-run" = { compute = "lambda" }

        # The Map body, one invocation per chunk. Lambda rather than "both" because a Fargate task
        # per chunk would pay a container cold start each time, and ecs:runTask.sync cannot return
        # the row count the next state needs.
        "classify" = { compute = "lambda" }

        # Runs once per wave: reads what that wave did and builds the next, or ends the loop.
        "next-wave" = { compute = "lambda" }

        # Runs once, after the last wave: closes the workflow row.
        "finalize" = { compute = "lambda" }

        # On the Catch path: closes the workflow row as failed. A step cannot record its own timeout
        # or OOM kill, so the state machine records it instead — see RecordFailure in
        # asl/atre.asl.json.
        "record-failure" = { compute = "lambda" }

        # On the Map item's Catch path: writes down the window a failed chunk never classified, so
        # the run can tolerate it and carry on without losing track of what was skipped.
        "record-chunk-failure" = { compute = "lambda" }
      }

      # No workflow-declared buckets, and no Map results bucket either: ATRE's Map hands its item
      # outputs back inline because next-wave has to read them, and a ResultWriter would replace
      # them. modules/workflow creates that bucket only when the definition names it.
      buckets = {}

      environment_variables = merge(local.workflow_common_environment, {
        # Rows one Map item reads, and so how long one invocation runs. Environment configuration
        # because the right number depends on this environment's Aurora capacity; AtreWaves falls
        # back to its own defaults when either is unset.
        THOR_ATRE_ACCOUNTS_PER_CHUNK = try(var.workflows["atre"].settings.accounts_per_chunk, "10000")

        # Chunks handed to one Map. Keep it equal to map_max_concurrency below, or a wave leaves
        # concurrency idle.
        THOR_ATRE_CHUNKS_PER_WAVE = try(var.workflows["atre"].settings.chunks_per_wave, "5")
      })

      # The Map's MaxConcurrency. Each classify invocation opens a read and a write connection, so
      # this bounds concurrent connections through the RDS Proxy at twice its value — a statement
      # about Aurora capacity rather than about ATRE's shape, which is why it is configuration and
      # not a literal in the definition.
      definition_vars = {
        map_max_concurrency = try(var.workflows["atre"].settings.map_max_concurrency, "5")
      }

      # Started by ingestion, not by an upload. Declaring no trigger is what stops
      # modules/workflow_trigger_s3 being instantiated for it at all.
      trigger           = null
      chained_workflows = []
    }

    reconcile = {
      # All Lambda, shaped like the graph load: start-run and next-wave hand the state machine the wave
      # it maps over. Started once per scan by the scan-completion gate, before that scan's graph load,
      # which applies these deletions to Neptune. See asl/reconcile.asl.json and
      # docs/architecture/reconcile_workflow.md.
      steps = {
        # Runs once: opens the scan's workflow row, decides per source and entity type, records every
        # decision and stages the deletions in Postgres. Must be lambda — the state machine reads the
        # first wave off its response.
        "start-run" = { compute = "lambda" }

        # The Map body: soft-deletes one window of staged rows in one transaction.
        "delete-chunk" = { compute = "lambda" }

        # Once per wave: builds the next wave from the staged total it is handed, or ends the loop.
        "next-wave" = { compute = "lambda" }

        # Once, after the last wave: drops the scan's ledger and staging and closes the row.
        "finalize" = { compute = "lambda" }

        # On the Catch path: closes the row as failed, which lets the gate go on to the graph load.
        "record-failure" = { compute = "lambda" }
      }

      # Staging lives in Postgres (reconcile_pending) and the Map hands nothing back that a later state
      # reads, so it needs no bucket.
      buckets = {}

      environment_variables = merge(local.workflow_common_environment, {
        # Staged rows one Map item deletes, and windows handed to one Map (ReconcileSettings), neither
        # measured yet. Keep chunks_per_wave equal to map_max_concurrency below, or a wave leaves
        # concurrency idle.
        THOR_RECONCILE_ROWS_PER_CHUNK  = try(var.workflows["reconcile"].settings.rows_per_chunk, "5000")
        THOR_RECONCILE_CHUNKS_PER_WAVE = try(var.workflows["reconcile"].settings.chunks_per_wave, "4")
      })

      # The Map's MaxConcurrency — each delete-chunk invocation holds one tenant database connection.
      definition_vars = {
        map_max_concurrency = try(var.workflows["reconcile"].settings.map_max_concurrency, "4")
      }

      # Started by the scan-completion gate (module.scan_gate in workflows.tf), which starts the graph
      # load once this run is terminal — so this workflow chains to nothing.
      trigger           = null
      chained_workflows = []
    }

    "graph-load" = {
      # All Lambda, shaped like ATRE: start-run and next-wave hand the state machine the wave it maps
      # over, and a Fargate task per chunk would pay a container cold start each time. Started once per
      # scan by the scan-completion gate (modules/scan_gate), not by an upload. See
      # asl/graph-load.asl.json.
      steps = {
        # Runs once: opens the scan's workflow row, lists what the scan changed and writes the plan.
        "start-run" = { compute = "lambda" }

        # The Map body: reads one chunk's rows from Postgres and upserts them into Neptune.
        "write-chunk" = { compute = "lambda" }

        # Once per wave: reads the plan and hands back the next wave, or ends the loop.
        "next-wave" = { compute = "lambda" }

        # Once, after the last wave: removes what the scan soft-deleted and closes the row.
        "finalize" = { compute = "lambda" }

        # On the Catch path: closes the row as failed.
        "record-failure" = { compute = "lambda" }
      }

      # The plan and the files of ids the waves map over — written by start-run, read by every chunk.
      # 14 days so a failed execution can still be redriven from where it stopped.
      buckets = {
        graphload = {
          retention_days = 14
          write_roles    = ["default"]
          read_roles     = ["default"]
          env_var        = "THOR_GRAPH_LOAD_BUCKET"
        }
      }

      environment_variables = merge(local.workflow_common_environment, {
        THOR_NEPTUNE_ENDPOINT  = var.enable_neptune ? module.neptune[0].endpoint : ""
        THOR_NEPTUNE_PORT      = "8182"
        THOR_NEPTUNE_ENABLESSL = "true"

        # The graph load's dials (GraphLoadSettings), configuration because the right values depend
        # on this environment's Neptune and Aurora capacity. Ids per chunk sizes one Map item's Lambda
        # invocation; parallelism is the requests one chunk keeps in flight. Keep chunks_per_wave
        # equal to map_max_concurrency below, or a wave leaves concurrency idle.
        THOR_GRAPH_LOAD_IDS_PER_CHUNK     = try(var.workflows["graph-load"].settings.ids_per_chunk, "10000")
        THOR_GRAPH_LOAD_CHUNKS_PER_WAVE   = try(var.workflows["graph-load"].settings.chunks_per_wave, "8")
        THOR_GRAPH_LOAD_WRITE_PARALLELISM = try(var.workflows["graph-load"].settings.write_parallelism, "4")
      })

      definition_vars = {
        # The Map's MaxConcurrency. Times THOR_GRAPH_LOAD_WRITE_PARALLELISM, it is how many requests
        # one scan keeps in flight against the Neptune writer — the share every concurrent scan, any
        # tenant's, gets of it. Each chunk also holds one tenant database connection.
        map_max_concurrency = try(var.workflows["graph-load"].settings.map_max_concurrency, "8")
      }

      # Started by the scan-completion gate, which also starts PAI once this run has succeeded and
      # ATRE and Ownership are done — so this workflow chains to nothing.
      trigger           = null
      chained_workflows = []
    }

    ownership = {
      # All Lambda, shaped like ATRE: a Distributed Map runs a wave of chunks, each reading its own
      # window of one phase's entities. Three phases — account, grp, asset — run one after another
      # inside the same loop, and each opens with the walk step. See asl/ownership.asl.json.
      steps = {
        # Runs once: validates the request, opens the workflow row, seeds the rule catalog, and hands
        # back the first wave. Must be lambda — the state machine reads that wave off its response.
        "start-run" = { compute = "lambda" }

        # Once per phase, before its first wave: propagates owners down the hierarchies the walk
        # rules follow. Lambda because it answers IsInProgress, which the state machine branches on.
        "walk" = { compute = "lambda" }

        # The Map body, one invocation per chunk. Lambda for ATRE's reasons: no container cold start
        # per chunk, and next-wave has to read the row count it returns.
        "vote" = { compute = "lambda" }

        # Once per wave: reads what the wave did and builds the next, or ends the loop.
        "next-wave" = { compute = "lambda" }

        # Runs once, after the last phase: closes the workflow row and drops the run's walk staging.
        # Ownership writes nothing to Neptune: its winners are proposals, and only an accepted
        # ownership belongs in the graph.
        "finalize" = { compute = "lambda" }

        # The Catch paths, as ATRE's.
        "record-failure"       = { compute = "lambda" }
        "record-chunk-failure" = { compute = "lambda" }
      }

      # Where the OWNED_BY bulk-load payloads went when Ownership still wrote its proposals to the
      # graph. Nothing writes it now. Kept only because Terraform cannot delete a bucket that still
      # holds objects: remove it — and "ownership" from neptune_bulk_load_workflows — once its 14-day
      # lifecycle has emptied it. No Map results bucket: the Map hands its outputs back inline.
      buckets = {
        graphload = {
          retention_days = 14
          write_roles    = ["graph-load"]
          env_var        = "THOR_GRAPH_BULKLOAD_BUCKET"
        }
      }

      environment_variables = merge(local.workflow_common_environment, {
        # Entities one Map item reads, and chunks handed to one Map — ATRE's two dials, for the same
        # reason: the right numbers depend on this environment's Aurora capacity. OwnershipWaves falls
        # back to its own defaults when either is unset. Keep chunks_per_wave equal to
        # map_max_concurrency below, or a wave leaves concurrency idle.
        THOR_OWNERSHIP_ENTITIES_PER_CHUNK = try(var.workflows["ownership"].settings.entities_per_chunk, "10000")
        THOR_OWNERSHIP_CHUNKS_PER_WAVE    = try(var.workflows["ownership"].settings.chunks_per_wave, "5")

        # How long one walk invocation keeps starting new levels: ten minutes of the fifteen-minute
        # Lambda timeout, leaving the rest for the level already running when it expires.
        THOR_OWNERSHIP_WALK_BUDGET_SECONDS = try(var.workflows["ownership"].settings.walk_budget_seconds, "600")
      })

      # The Map's MaxConcurrency — each vote invocation holds a read and a write connection, so this
      # bounds connections through the RDS Proxy at twice its value.
      definition_vars = {
        map_max_concurrency = try(var.workflows["ownership"].settings.map_max_concurrency, "5")
      }

      # Started by ingestion (or a caller with a RunId), not by an upload.
      trigger           = null
      chained_workflows = []
    }

    pai = {
      # All Lambda, shaped like Ownership: a run stages the targets it recomputes, expands the group
      # layer below them level by level, then a Distributed Map writes routes in waves. See
      # asl/pai.asl.json and docs/architecture/pai_workflow.md.
      steps = {
        # Runs once: opens the workflow row, seeds definitions, stages this run's targets and hands back
        # the first wave. Must be lambda — the state machine reads that wave off its response.
        "start-run" = { compute = "lambda" }

        # Loops until done. Lambda because it answers IsInProgress, which the state machine branches on.
        "expand" = { compute = "lambda" }

        # The Map body, one invocation per window of staged paths; next-wave reads its path count.
        "fanout" = { compute = "lambda" }

        # Once per wave: reads what the wave did and builds the next, or ends the loop.
        "next-wave" = { compute = "lambda" }

        # Runs once, after the last wave: prunes, closes the workflow row, drops the run's staging.
        "finalize" = { compute = "lambda" }

        # The Catch paths, as ATRE's and Ownership's.
        "record-failure"       = { compute = "lambda" }
        "record-chunk-failure" = { compute = "lambda" }
      }

      # PAI writes nothing to Neptune and hands its Map outputs back inline, so it needs no bucket.
      buckets = {}

      environment_variables = merge(local.workflow_common_environment, {
        # The reader endpoint: PAI only traverses, and this keeps it off the writer that ingestion's and
        # Ownership's bulk loads use.
        THOR_NEPTUNE_ENDPOINT  = var.enable_neptune ? module.neptune[0].reader_endpoint : ""
        THOR_NEPTUNE_PORT      = "8182"
        THOR_NEPTUNE_ENABLESSL = "true"

        # Paths one Map item reads, and chunks handed to one Map. PaiWaves falls back to its own defaults
        # when either is unset; none has been measured. Keep chunks_per_wave equal to map_max_concurrency.
        THOR_PAI_PATHS_PER_CHUNK = try(var.workflows["pai"].settings.paths_per_chunk, "500")
        THOR_PAI_CHUNKS_PER_WAVE = try(var.workflows["pai"].settings.chunks_per_wave, "5")

        # How long one expand invocation keeps starting levels: ten minutes of the fifteen-minute timeout.
        THOR_PAI_EXPAND_BUDGET_SECONDS = try(var.workflows["pai"].settings.expand_budget_seconds, "600")

        # Group paths staged per target before truncating with a warning — a guard against a
        # pathological diamond lattice, not a limit real nesting should reach.
        THOR_PAI_MAX_PATHS_PER_TARGET = try(var.workflows["pai"].settings.max_paths_per_target, "10000")
      })

      # The Map's MaxConcurrency — each fanout invocation holds a read and a write connection.
      definition_vars = {
        map_max_concurrency = try(var.workflows["pai"].settings.map_max_concurrency, "5")
      }

      # Started by the scan-completion gate (module.scan_gate in workflows.tf), not by an upload or by
      # another workflow's final state.
      trigger           = null
      chained_workflows = []
    }
  }
}
