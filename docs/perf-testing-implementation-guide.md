# Performance-testing pipeline — implementation guide

Instructions to add the JMeter performance-testing pipeline (built and proven in `thor-backend-nat`) to another
repository with the same folder structure. Written so Claude (or an engineer) can implement it step by step.

**Do not guess values.** Every `<PLACEHOLDER>` must be filled from the target repo or AWS account — ask the user if
you can't find one. Work through the steps in order, and stop at each **CHECK** if it fails.

---

## What you are building

QA runs a load test from GitHub (**Actions → Performance Test → Run workflow**) with no AWS access. The workflow:

```
[1/7] Show and validate the 5 load inputs
[2/7] Make sure JMeter is cached in S3 (the GitHub runner downloads it from Apache only the first time)
[3/7] Upload ONLY the run script, test plan, config, test data and zip-pool to s3://<bucket>/runs/<run id>/inputs/
[4/7] Launch a temporary EC2 instance from a launch template, with perf-tests/run-on-instance.sh as user data
        On the instance:
        [1/6] start, set a hard time limit
        [2/6] install Java (Amazon Corretto 21)
        [3/6] copy JMeter from S3
        [4/6] copy inputs/ from S3 into /opt/perf/tests (same layout as perf-tests/)
        [5/6] run the run script (perf-tests/run-dryrun.sh) with the 5 inputs as -J overrides
        [6/6] rename outputs, zip the HTML report, upload results to S3, write DONE, terminate itself
[5/7] Wait for the DONE marker in S3
[6/7] Write a results table on the GitHub run's Summary page
[7/7] Terminate the instance (safety net, always runs)
```

| Piece | Where | Managed by |
|---|---|---|
| S3 results bucket, launch template, instance role, GitHub role | `infra/src/modules/perf_test/` | Terraform (Terragrunt envs) |
| Networking (VPC, private subnet, NAT, DNS Firewall) | existing `infra/src/modules/network` | already in the repo — not changed |
| GitHub OIDC provider | AWS account | already exists — only looked up |
| Workflow | `.github/workflows/performance-test.yml` | GitHub Actions |
| Test assets + instance script | `perf-tests/` | repo |

**Rule:** infrastructure changes go through Terraform only. The workflow never creates or changes infrastructure; it
only launches the temporary instance from the launch template and reads/writes the bucket.

---

## Step 0 — Gather facts and check prerequisites

Collect these before writing anything. Ask the user for anything you can't determine.

| Placeholder | How to find it |
|---|---|
| `<ENV>` | Terragrunt environment to deploy to (e.g. `dev`): a folder under `infra/envs/` |
| `<GITHUB_ENVIRONMENT>` | GitHub environment the workflow runs in (e.g. `dev` or `prod`); create it in GitHub if missing |
| `<GITHUB_SUB_PREFIX>` | `gh api repos/<owner>/<repo>/actions/oidc/customization/sub --jq .sub_claim_prefix` (e.g. `repo:Owner@123/repo@456`). If `use_immutable_subject` is false it is `repo:<owner>/<repo>` |
| `<AWS_PROFILE>` | AWS CLI profile for the target account |

**CHECK each of these, and stop if one fails:**

1. **GitHub OIDC provider exists** in the target account:
   `aws iam list-open-id-connect-providers` lists `.../oidc-provider/token.actions.githubusercontent.com`.
2. **The network module exposes** `vpc_id` and `private_subnet_ids` outputs (`infra/src/modules/network/outputs.tf`).
3. **The env has NAT egress**: `nat_gateway_mode` is not `"none"` in `infra/envs/<ENV>/terragrunt.hcl`. The instance
   runs in a private subnet and must reach Amazon Linux's package repos and the test target. If it is `"none"`, stop
   and ask the user — turning NAT on changes the whole environment.
4. **DNS Firewall allowlist** (only if `dns_firewall_default_action = "BLOCK"`): the test target's domain and
   `cdn.amazonlinux.com` must be allowed (`egress_allowed_domains`). `*.amazonaws.com` (S3, API Gateway) is already
   allowed by the module. In `"ALERT"` mode nothing is blocked.
5. **Instance type is allowed** by the account's SCP. Run, after Step 3's apply (or against any launch template):
   `aws ec2 run-instances --dry-run --launch-template LaunchTemplateId=<id>,Version='$Latest' --instance-type <type> --tag-specifications 'ResourceType=instance,Tags=[{Key=Purpose,Value=perf-test}]'`
   → `DryRunOperation` means allowed; "explicit deny in a service control policy" means pick another type. (In the
   original sandbox account c5n.\*, m5n, c6i, m6i, c7i were all SCP-blocked; t3.xlarge was allowed.)
6. **The permissions boundary** `thor-<ENV>-role-boundary` (used by `local.iam_permissions_boundary_arn` in
   `infra/src/main.tf`) must allow: `ec2:RunInstances`, `ec2:CreateTags`, `ec2:TerminateInstances`,
   `ec2:Describe*`, `iam:PassRole`, `s3:GetObject/PutObject/ListBucket`. If it doesn't, the roles are created but
   calls fail with AccessDenied.
7. **Root `.gitignore`**: if it ignores `*.zip` (the original repo did, at line 454), the ZIP pool needs the exception
   in Step 4 or the ZIPs silently never get committed.

---

## Step 1 — Terraform module `infra/src/modules/perf_test/`

Create these four files exactly. They follow the repo's conventions (Cloud Custodian tag `ignore_changes`, the
shared permissions boundary, names `thor-<env>-*`). This code passed `terraform validate`.

### `infra/src/modules/perf_test/main.tf`

```hcl
# JMeter performance-test load generator, driven by .github/workflows/performance-test.yml. The workflow launches
# one EC2 instance per run from the launch template below; the instance runs the test, writes results to the bucket,
# and terminates itself. Uses the environment's existing VPC (module.network) — this module adds no networking.

data "aws_ssm_parameter" "al2023" {
  name = "/aws/service/ami-amazon-linux-latest/al2023-ami-kernel-default-x86_64"
}

locals {
  name = "thor-${var.environment}-perf"
}

# --- results bucket ----------------------------------------------------------------------------------------------
# tools/apache-jmeter-<v>.tgz   uploaded once by the workflow (the instance never downloads from Apache); kept
# runs/<run id>/inputs/         uploaded by the workflow: run script, test plan, config, test data, zip-pool
# runs/<run id>/results/        written by the instance: report/, report.zip, results.jtl, jmeter.log, run.log
# runs/<run id>/DONE            written by the instance last: "exit=<code>"
# Everything under runs/ expires after var.results_retention_days.

resource "aws_s3_bucket" "results" {
  bucket = "${local.name}-results-${var.account_id}"

  # Cloud Custodian auto-tags this after creation and an SCP blocks removing it — ignore tags to avoid fighting it.
  lifecycle {
    ignore_changes = [tags, tags_all]
  }
}

resource "aws_s3_bucket_public_access_block" "results" {
  bucket                  = aws_s3_bucket.results.id
  block_public_acls       = true
  block_public_policy     = true
  ignore_public_acls      = true
  restrict_public_buckets = true
}

resource "aws_s3_bucket_server_side_encryption_configuration" "results" {
  bucket = aws_s3_bucket.results.id

  rule {
    apply_server_side_encryption_by_default {
      sse_algorithm = "AES256"
    }
  }
}

resource "aws_s3_bucket_lifecycle_configuration" "results" {
  bucket = aws_s3_bucket.results.id

  rule {
    id     = "expire-runs"
    status = "Enabled"

    filter {
      prefix = "runs/"
    }

    expiration {
      days = var.results_retention_days
    }
  }
}

resource "aws_s3_bucket_policy" "results_tls_only" {
  bucket = aws_s3_bucket.results.id

  policy = jsonencode({
    Version = "2012-10-17"
    Statement = [{
      Sid       = "DenyNonTLS"
      Effect    = "Deny"
      Principal = "*"
      Action    = "s3:*"
      Resource  = [aws_s3_bucket.results.arn, "${aws_s3_bucket.results.arn}/*"]
      Condition = { Bool = { "aws:SecureTransport" = "false" } }
    }]
  })

  depends_on = [aws_s3_bucket_public_access_block.results]
}

# --- load-generator instance -------------------------------------------------------------------------------------

resource "aws_security_group" "jmeter" {
  name        = "${local.name}-jmeter"
  description = "JMeter load generator: outbound HTTPS only, nothing inbound"
  vpc_id      = var.vpc_id

  egress {
    description = "HTTPS to the target, S3 and the dnf repos (DNS to the VPC resolver is not SG-filtered)"
    from_port   = 443
    to_port     = 443
    protocol    = "tcp"
    cidr_blocks = ["0.0.0.0/0"]
  }

  # Cloud Custodian auto-tags this after creation and an SCP blocks removing it — ignore tags to avoid fighting it.
  lifecycle {
    ignore_changes = [tags, tags_all]
  }
}

resource "aws_iam_role" "jmeter" {
  name                 = "${local.name}-jmeter"
  permissions_boundary = var.iam_permissions_boundary_arn

  assume_role_policy = jsonencode({
    Version = "2012-10-17"
    Statement = [{
      Effect    = "Allow"
      Principal = { Service = "ec2.amazonaws.com" }
      Action    = "sts:AssumeRole"
    }]
  })

  # Cloud Custodian auto-tags this after creation and an SCP blocks removing it — ignore tags to avoid fighting it.
  lifecycle {
    ignore_changes = [tags, tags_all]
  }
}

# Read JMeter and this run's inputs, write this run's results — nothing else.
resource "aws_iam_role_policy" "jmeter_s3" {
  name = "results-bucket"
  role = aws_iam_role.jmeter.id

  policy = jsonencode({
    Version = "2012-10-17"
    Statement = [
      {
        Effect = "Allow"
        Action = "s3:GetObject"
        Resource = [
          "${aws_s3_bucket.results.arn}/tools/*",
          "${aws_s3_bucket.results.arn}/runs/*/inputs/*",
        ]
      },
      {
        # `aws s3 cp --recursive` lists the run's inputs/ prefix before downloading it.
        Effect    = "Allow"
        Action    = "s3:ListBucket"
        Resource  = aws_s3_bucket.results.arn
        Condition = { StringLike = { "s3:prefix" = "runs/*/inputs/*" } }
      },
      {
        Effect   = "Allow"
        Action   = "s3:PutObject"
        Resource = ["${aws_s3_bucket.results.arn}/runs/*/results/*", "${aws_s3_bucket.results.arn}/runs/*/DONE"]
      },
    ]
  })
}

resource "aws_iam_instance_profile" "jmeter" {
  name = "${local.name}-jmeter"
  role = aws_iam_role.jmeter.name

  # Cloud Custodian auto-tags this after creation and an SCP blocks removing it — ignore tags to avoid fighting it.
  lifecycle {
    ignore_changes = [tags, tags_all]
  }
}

# The workflow launches from this (with the run's user data). Changing the instance type or disk here is the only way
# to resize the load generator; every change becomes the template's default version.
resource "aws_launch_template" "jmeter" {
  name                                 = "${local.name}-jmeter"
  image_id                             = nonsensitive(data.aws_ssm_parameter.al2023.value)
  instance_type                        = var.instance_type
  instance_initiated_shutdown_behavior = "terminate" # the run script's final `shutdown` deletes the instance
  update_default_version               = true        # each change becomes the default, not just $Latest

  iam_instance_profile {
    arn = aws_iam_instance_profile.jmeter.arn
  }

  network_interfaces {
    subnet_id                   = var.subnet_id
    security_groups             = [aws_security_group.jmeter.id]
    associate_public_ip_address = false
    delete_on_termination       = true
  }

  metadata_options {
    http_tokens                 = "required" # IMDSv2 only
    http_put_response_hop_limit = 1
  }

  block_device_mappings {
    device_name = "/dev/xvda"

    ebs {
      volume_size           = var.root_volume_gb
      volume_type           = "gp3"
      encrypted             = true
      delete_on_termination = true
    }
  }

  # Cloud Custodian auto-tags this after creation and an SCP blocks removing it — ignore tags to avoid fighting it.
  lifecycle {
    ignore_changes = [tags, tags_all]
  }
}
```

### `infra/src/modules/perf_test/github.tf`

```hcl
# The one role .github/workflows/performance-test.yml assumes, through the account's existing GitHub OIDC provider.

data "aws_iam_openid_connect_provider" "github" {
  url = "https://token.actions.githubusercontent.com"
}

resource "aws_iam_role" "github" {
  name                 = "${local.name}-github"
  permissions_boundary = var.iam_permissions_boundary_arn

  # Only the workflow running in this repo's var.github_environment GitHub environment. The subject is GitHub's
  # immutable form (owner and repo ids), so a renamed or re-created repo of the same name can't assume this.
  assume_role_policy = jsonencode({
    Version = "2012-10-17"
    Statement = [{
      Effect    = "Allow"
      Principal = { Federated = data.aws_iam_openid_connect_provider.github.arn }
      Action    = "sts:AssumeRoleWithWebIdentity"
      Condition = {
        StringEquals = {
          "token.actions.githubusercontent.com:aud" = "sts.amazonaws.com"
          "token.actions.githubusercontent.com:sub" = "${var.github_sub_prefix}:environment:${var.github_environment}"
        }
      }
    }]
  })

  # Cloud Custodian auto-tags this after creation and an SCP blocks removing it — ignore tags to avoid fighting it.
  lifecycle {
    ignore_changes = [tags, tags_all]
  }
}

resource "aws_iam_role_policy" "github" {
  name = "run-perf-test"
  role = aws_iam_role.github.id

  policy = jsonencode({
    Version = "2012-10-17"
    Statement = [
      {
        Sid      = "LaunchOnlyFromTheTemplate"
        Effect   = "Allow"
        Action   = "ec2:RunInstances"
        Resource = "arn:aws:ec2:${var.aws_region}:${var.account_id}:instance/*"
        Condition = {
          ArnEquals    = { "ec2:LaunchTemplate" = aws_launch_template.jmeter.arn }
          StringEquals = { "aws:RequestTag/Purpose" = "perf-test" }
        }
      },
      {
        Sid    = "LaunchResources"
        Effect = "Allow"
        Action = "ec2:RunInstances"
        Resource = [
          aws_launch_template.jmeter.arn,
          "arn:aws:ec2:${var.aws_region}::image/*",
          "arn:aws:ec2:${var.aws_region}:${var.account_id}:subnet/${var.subnet_id}",
          aws_security_group.jmeter.arn,
          "arn:aws:ec2:${var.aws_region}:${var.account_id}:network-interface/*",
          "arn:aws:ec2:${var.aws_region}:${var.account_id}:volume/*",
        ]
      },
      {
        Sid       = "TagAtLaunch"
        Effect    = "Allow"
        Action    = "ec2:CreateTags"
        Resource  = "arn:aws:ec2:${var.aws_region}:${var.account_id}:*/*"
        Condition = { StringEquals = { "ec2:CreateAction" = "RunInstances" } }
      },
      {
        Sid      = "PassInstanceRole"
        Effect   = "Allow"
        Action   = "iam:PassRole"
        Resource = aws_iam_role.jmeter.arn
      },
      {
        Sid       = "TerminateOnlyPerfTestInstances"
        Effect    = "Allow"
        Action    = "ec2:TerminateInstances"
        Resource  = "arn:aws:ec2:${var.aws_region}:${var.account_id}:instance/*"
        Condition = { StringEquals = { "aws:ResourceTag/Purpose" = "perf-test" } }
      },
      {
        Sid      = "Describe"
        Effect   = "Allow"
        Action   = ["ec2:DescribeInstances", "ec2:DescribeInstanceStatus", "ec2:DescribeLaunchTemplateVersions"]
        Resource = "*"
      },
      {
        Sid      = "ResultsBucketList"
        Effect   = "Allow"
        Action   = "s3:ListBucket"
        Resource = aws_s3_bucket.results.arn
      },
      {
        Sid      = "ResultsBucketObjects"
        Effect   = "Allow"
        Action   = ["s3:GetObject", "s3:PutObject"]
        Resource = ["${aws_s3_bucket.results.arn}/tools/*", "${aws_s3_bucket.results.arn}/runs/*"]
      },
    ]
  })
}
```

### `infra/src/modules/perf_test/variables.tf`

```hcl
variable "environment" {
  type        = string
  description = "Environment name (dev, qa, prod) — resource names are thor-<env>-perf-*"
}

variable "aws_region" {
  type        = string
  description = "Region the load generator runs in"
}

variable "account_id" {
  type        = string
  description = "AWS account ID"
}

variable "vpc_id" {
  type        = string
  description = "VPC the load generator runs in (module.network.vpc_id)"
}

variable "subnet_id" {
  type        = string
  description = "Private subnet the load generator runs in; must route 0.0.0.0/0 through a NAT gateway"
}

variable "iam_permissions_boundary_arn" {
  type        = string
  description = "Permissions boundary attached to both roles (same one every other module uses)"
}

variable "instance_type" {
  type        = string
  description = "Load generator instance type. Check the account's SCP allows it (aws ec2 run-instances --dry-run)"
  default     = "t3.xlarge"
}

variable "root_volume_gb" {
  type        = number
  description = "gp3 root disk for JMeter, the ZIP pool, logs and results"
  default     = 50
}

variable "results_retention_days" {
  type        = number
  description = "Days before a run's files under runs/ are deleted"
  default     = 30
}

variable "github_sub_prefix" {
  type        = string
  description = "The repo's GitHub OIDC subject prefix: gh api repos/<owner>/<repo>/actions/oidc/customization/sub (sub_claim_prefix)"
}

variable "github_environment" {
  type        = string
  description = "GitHub environment the workflow job runs in — the only one allowed to assume the role"
}
```

### `infra/src/modules/perf_test/outputs.tf`

```hcl
# The four values the workflow needs, set as variables on its GitHub environment.

output "github_role_arn" {
  value = aws_iam_role.github.arn
}

output "results_bucket" {
  value = aws_s3_bucket.results.bucket
}

output "launch_template_id" {
  value = aws_launch_template.jmeter.id
}
```

---

## Step 2 — Wire the module into the root module (`infra/src`)

### Add to `infra/src/variables.tf`

```hcl
variable "enable_perf_test" {
  type        = bool
  description = "Create the JMeter performance-test load generator (module.perf_test). Needs nat_gateway_mode != \"none\""
  default     = false
}

variable "perf_test_instance_type" {
  type        = string
  description = "Load generator instance type (must be allowed by the account's SCP)"
  default     = "t3.xlarge"
}

variable "perf_test_github_sub_prefix" {
  type        = string
  description = "GitHub OIDC subject prefix of this repo (gh api repos/<owner>/<repo>/actions/oidc/customization/sub)"
  default     = ""
}

variable "perf_test_github_environment" {
  type        = string
  description = "GitHub environment performance-test.yml runs in"
  default     = ""
}
```

### Add to `infra/src/main.tf`

```hcl
# JMeter performance-test load generator (.github/workflows/performance-test.yml). Uses the existing VPC.
module "perf_test" {
  count  = var.enable_perf_test ? 1 : 0
  source = "./modules/perf_test"

  environment                  = var.environment
  aws_region                   = var.aws_region
  account_id                   = var.account_id
  vpc_id                       = module.network.vpc_id
  subnet_id                    = module.network.private_subnet_ids[0]
  iam_permissions_boundary_arn = local.iam_permissions_boundary_arn
  instance_type                = var.perf_test_instance_type
  github_sub_prefix            = var.perf_test_github_sub_prefix
  github_environment           = var.perf_test_github_environment
}
```

### Add to `infra/src/outputs.tf`

```hcl
# Set these as variables on the GitHub environment performance-test.yml runs in.
output "perf_test_github_role_arn" {
  value = var.enable_perf_test ? module.perf_test[0].github_role_arn : ""
}

output "perf_test_results_bucket" {
  value = var.enable_perf_test ? module.perf_test[0].results_bucket : ""
}

output "perf_test_launch_template_id" {
  value = var.enable_perf_test ? module.perf_test[0].launch_template_id : ""
}
```

Run `terraform fmt -recursive` in `infra/src` and `terraform validate` (or the repo's tflint) before moving on.

---

## Step 3 — Enable it in the environment

In `infra/envs/<ENV>/terragrunt.hcl`, inside `inputs = { ... }` (the repo spells out every input there — keep that
style and add the new ones in a commented block):

```hcl
  # --- performance test (JMeter load generator, .github/workflows/performance-test.yml) ---
  enable_perf_test             = true
  perf_test_instance_type      = "t3.xlarge"
  perf_test_github_sub_prefix  = "<GITHUB_SUB_PREFIX>"
  perf_test_github_environment = "<GITHUB_ENVIRONMENT>"
```

Other environments: add the same keys with `enable_perf_test = false`, so every env file stays explicit.

**Plan, show the plan to the user, and apply only after they approve.** Expected: about 11 new resources (bucket +
public-access block + encryption + lifecycle + policy, security group, 2 IAM roles + 2 policies, instance profile,
launch template) and nothing else changed.

---

## Step 4 — `perf-tests/` folder

Final layout:

```
perf-tests/
├── .gitignore
├── run-on-instance.sh              EC2 user data (from this guide)
├── run-dryrun.sh                   the run script the instance runs (RUN_SCRIPT)
├── run.sh                          local EC2-style runner (optional, copied as-is)
├── test-plan_smoke.jmx             JMeter test plan (TEST_PLAN)
├── config/
│   ├── config.dryrun.properties    config the run script uses (CONFIG_FILE)
│   ├── config.ec2.properties       optional, copied as-is
│   └── test-data.csv               client/API key/task IDs the plan reads (TEST_DATA)
└── zip-pool/                       ZIPs the plan uploads (ZIP_POOL)
    └── AD|windows|CyberArc/task_01.zip … task_10.zip
```

Copy `test-plan_smoke.jmx`, `config/*`, `zip-pool/`, `run.sh` and `run-dryrun.sh` **as-is** from the source repo
(`thor-backend-nat/perf-tests/`), unless the user provides new ones. Create the two files below.

> `config/test-data.csv` contains real API keys. In the original (public) repo the user chose to commit it — confirm
> with the user before committing it in the target repo, especially if that repo is public.

### `perf-tests/.gitignore`

```gitignore
# The repo root ignores *.zip; the test plan uploads these, so they must be committed.
!zip-pool/**/*.zip

# JMeter run output (.jtl results, logs, HTML reports)
results/
```

### `perf-tests/run-on-instance.sh`

```bash
#!/bin/bash
# EC2 user data for one performance-test run, launched by .github/workflows/performance-test.yml. The workflow
# prepends the run's settings as shell variables:
#   BUCKET RUN_ID JMETER_VERSION JMETER_HEAP MAX_MINUTES RUN_SCRIPT
#   THREAD_COUNT RAMP_UP_SECONDS TEST_DURATION_SECONDS THINK_TIME_MIN_MS THINK_TIME_MAX_MS
# Pulls Java (dnf), JMeter and the test inputs (run script, plan, config, test data, zip-pool) from S3, runs the
# run script, writes results/ and finally DONE to s3://$BUCKET/runs/$RUN_ID/, then shuts down — which terminates the
# instance (launch template setting). Everything printed here ends up in results/run.log in S3.
set -euo pipefail

RUN_URI="s3://${BUCKET}/runs/${RUN_ID}"
WORK=/opt/perf
TESTS="$WORK/tests" # same layout as perf-tests/: the run script, plan, config/, zip-pool/
mkdir -p "$WORK/results"
exec > >(tee "$WORK/results/run.log") 2>&1

log() { echo "$(date -u +%H:%M:%S) $*"; }

log "▶ [1/6] Instance started for run ${RUN_ID}"
log "  CPUs: $(nproc), memory: $(free -g | awk '/Mem:/ {print $2}') GiB, free disk: $(df -h / | awk 'NR==2 {print $4}')"
log "  Load: ${THREAD_COUNT} users, ${RAMP_UP_SECONDS}s ramp-up, ${TEST_DURATION_SECONDS}s duration," \
  "think time ${THINK_TIME_MIN_MS}-${THINK_TIME_MAX_MS} ms"
log "  JMeter ${JMETER_VERSION}, heap ${JMETER_HEAP}, run script ${RUN_SCRIPT}"

# Hard cap: the instance terminates after MAX_MINUTES even if JMeter hangs.
shutdown -h "+${MAX_MINUTES}"
log "  Safety timer set: the instance terminates in ${MAX_MINUTES} min no matter what"

# The run script writes timestamped files to results/ (e.g. dryrun_<time>.jtl); give the main ones the fixed names
# the workflow's summary and report.zip use, and keep everything else as is.
collect_results() {
  local f
  for f in "$TESTS"/results/*; do
    [[ -e "$f" ]] || continue
    case "$(basename "$f")" in
      *_report_*)   mv "$f" "$WORK/results/report" ;;      # dryrun_report_<time>/ → report/
      *_[0-9]*.jtl) mv "$f" "$WORK/results/results.jtl" ;; # dryrun_<time>.jtl     → results.jtl
      *_[0-9]*.log) mv "$f" "$WORK/results/jmeter.log" ;;  # dryrun_<time>.log     → jmeter.log
      *)            mv "$f" "$WORK/results/" ;;            # e.g. the plan's own dryrun.jtl
    esac
  done
}

# The report is index.html plus its content/ and sbadmin2-*/ folders; the S3 console can't download a folder, so
# also ship it as one zip (Python's zipfile is already on Amazon Linux).
zip_report() {
  [[ -d "$WORK/results/report" ]] || return 0
  (cd "$WORK/results" && python3 -m zipfile -c report.zip report/)
  log "  Packed the HTML report into results/report.zip ($(du -h "$WORK/results/report.zip" | cut -f1))"
}

finish() {
  local code=$?
  log "▶ [6/6] Finishing (exit code ${code})"
  collect_results || true
  zip_report || true
  # Logged before the upload so these lines are in the run.log that reaches S3.
  log "  Uploading results to ${RUN_URI}/results/, then writing the DONE marker the GitHub workflow waits for"
  log "✔ After that the instance shuts down and terminates itself."
  aws s3 cp --recursive --only-show-errors "$WORK/results" "${RUN_URI}/results/" || true
  echo "exit=${code}" | aws s3 cp - "${RUN_URI}/DONE" || true
  shutdown -h now
}
trap finish EXIT

log "▶ [2/6] Installing Java (Amazon Corretto 21) with dnf"
dnf install -y -q java-21-amazon-corretto-headless
log "✔ $(java -version 2>&1 | head -1)"

log "▶ [3/6] Copying JMeter ${JMETER_VERSION} from s3://${BUCKET}/tools/"
aws s3 cp --only-show-errors "s3://${BUCKET}/tools/apache-jmeter-${JMETER_VERSION}.tgz" "$WORK/"
tar -xzf "$WORK/apache-jmeter-${JMETER_VERSION}.tgz" -C "$WORK"
log "✔ JMeter unpacked to $WORK/apache-jmeter-${JMETER_VERSION}"

log "▶ [4/6] Copying the run script, test plan, config, test data and zip-pool from ${RUN_URI}/inputs/"
aws s3 cp --recursive --only-show-errors "${RUN_URI}/inputs/" "$TESTS/"
log "✔ Test inputs ready: $(find "$TESTS/zip-pool" -name '*.zip' | wc -l) ZIPs in the pool"

# bin/jmeter reads its heap from HEAP (default -Xms1g -Xmx1g); the run script calls `jmeter` from the PATH.
export HEAP="$JMETER_HEAP"
export PATH="$WORK/apache-jmeter-${JMETER_VERSION}/bin:$PATH"

# Run from inside $TESTS: the run script and the test plan find their files by relative path. The -J values are the
# workflow inputs; the script passes them to JMeter, where they override the same keys in its config file.
log "▶ [5/6] Running ${RUN_SCRIPT} — this is the actual load test"
cd "$TESTS"
bash "./${RUN_SCRIPT}" \
  -Jthread.count="$THREAD_COUNT" \
  -Jramp.up.seconds="$RAMP_UP_SECONDS" \
  -Jtest.duration.seconds="$TEST_DURATION_SECONDS" \
  -Jthink.time.min.ms="$THINK_TIME_MIN_MS" \
  -Jthink.time.max.ms="$THINK_TIME_MAX_MS"
log "✔ ${RUN_SCRIPT} finished"
```

### Contract the run script must follow (`perf-tests/run-dryrun.sh`)

The instance runs `bash ./<RUN_SCRIPT> -J…` from inside the test folder. Any run script (including a renamed or new
one) must:

1. **Pass `"$@"` through to `jmeter`**, so the 5 workflow inputs apply.
2. **Call `jmeter` from the PATH** (the instance puts JMeter's `bin/` on it).
3. **Find its files relative to its own folder** (the instance keeps the `perf-tests/` layout).
4. **Name its outputs** `results/<name>_<timestamp>.jtl`, `results/<name>_<timestamp>.log` and
   `results/<name>_report_<timestamp>/` — `run-on-instance.sh` maps these to `results.jtl`, `jmeter.log` and
   `report/` (+ `report.zip`). Different names ⇒ no summary table and no report.zip.

Reference — the working `run-dryrun.sh`:

```bash
#!/usr/bin/env bash
# THOR Dry Run — local validation before EC2
# Usage: ./run-dryrun.sh [overrides...]
# Example: ./run-dryrun.sh -Jthread.count=10 -Jtest.duration.seconds=300

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
RESULTS_DIR="${SCRIPT_DIR}/results"
TIMESTAMP="$(date +%Y%m%d_%H%M%S)"
RESULT_FILE="${RESULTS_DIR}/dryrun_${TIMESTAMP}.jtl"
LOG_FILE="${RESULTS_DIR}/dryrun_${TIMESTAMP}.log"
REPORT_DIR="${RESULTS_DIR}/dryrun_report_${TIMESTAMP}"

mkdir -p "${RESULTS_DIR}"

# ── Verify prerequisites ─────────────────────────────────────────────────────
if ! command -v jmeter &>/dev/null; then
    echo "ERROR: jmeter not found in PATH. Install Apache JMeter and add it to PATH."
    exit 1
fi

if [[ ! -f "${SCRIPT_DIR}/config/test-data.csv" ]]; then
    echo "ERROR: config/test-data.csv not found. Fill in client and task data first."
    exit 1
fi

if [[ ! -d "${SCRIPT_DIR}/zip-pool" ]]; then
    echo "ERROR: zip-pool/ not found. Run: python ../generate_zip_pool.py"
    exit 1
fi

# ── Run JMeter ───────────────────────────────────────────────────────────────
echo "Starting THOR dry run at ${TIMESTAMP}"
echo "Threads : 5  |  Duration : 120s  |  Ramp-up : 10s"
echo "Results → ${RESULT_FILE}"
echo "Log     → ${LOG_FILE}"
echo ""

jmeter \
    -n \
    -t "${SCRIPT_DIR}/test-plan_smoke.jmx" \
    -p "${SCRIPT_DIR}/config/config.dryrun.properties" \
    -l "${RESULT_FILE}" \
    -j "${LOG_FILE}" \
    "$@"

echo ""
echo "Dry run complete. Generating HTML report..."

jmeter \
    -g "${RESULT_FILE}" \
    -o "${REPORT_DIR}"

echo "HTML report → ${REPORT_DIR}/index.html"
echo ""
echo "Check before EC2:"
echo "  - All Register API calls → HTTP 200"
echo "  - All Upload API calls   → HTTP 200 (presigned URL extracted)"
echo "  - All S3 PUT calls       → HTTP 200 or 204"
echo "  - Error rate             → 0%"
```

---

## Step 5 — Workflow `.github/workflows/performance-test.yml`

Create it as below, replacing `<ENV>` and `<GITHUB_ENVIRONMENT>`. It reads four GitHub **environment variables**
(Step 6): `PERF_AWS_ROLE_ARN`, `PERF_AWS_REGION`, `PERF_RESULTS_BUCKET`, `PERF_LAUNCH_TEMPLATE_ID`.

```yaml
name: Performance Test

# Manual JMeter load test from a throwaway EC2 instance (infra/src/modules/perf_test). Nobody logs in to the
# instance: it runs perf-tests/run-on-instance.sh at boot, writes results to S3 and terminates itself. The HTML
# report stays in the private results bucket only — it is never attached to the GitHub run.
# Instance type and disk come from the launch template (Terraform).
# Load settings come from the Run workflow inputs, which override the same keys in the config file (CONFIG_FILE).

on:
  workflow_dispatch:
    inputs:
      thread_count:
        description: Concurrent users (thread.count)
        type: string
        default: "5"
        required: true
      ramp_up_seconds:
        description: Seconds to start all users (ramp.up.seconds)
        type: string
        default: "10"
        required: true
      test_duration_seconds:
        description: How long the test runs, in seconds (test.duration.seconds)
        type: string
        default: "120"
        required: true
      think_time_min_ms:
        description: Minimum pause between requests, in ms (think.time.min.ms)
        type: string
        default: "0"
        required: true
      think_time_max_ms:
        description: Maximum pause between requests, in ms (think.time.max.ms)
        type: string
        default: "500"
        required: true

permissions:
  id-token: write
  contents: read

concurrency:
  group: performance-test
  cancel-in-progress: false

env:
  NAME_PREFIX: thor-<ENV>-perf # must match the perf_test module's names (thor-<environment>-perf)
  JMETER_VERSION: "5.6.3"
  JMETER_HEAP: "-Xms4g -Xmx8g -XX:MaxMetaspaceSize=256m" # fits t3.xlarge's 16 GiB; lower it for smaller instances
  # Test inputs sent to the instance (and nothing else from perf-tests/), all paths inside perf-tests/.
  # The instance runs RUN_SCRIPT, which refers to the plan and config by name: rename them there too.
  RUN_SCRIPT: perf-tests/run-dryrun.sh
  TEST_PLAN: perf-tests/test-plan_smoke.jmx
  CONFIG_FILE: perf-tests/config/config.dryrun.properties
  TEST_DATA: perf-tests/config/test-data.csv
  ZIP_POOL: perf-tests/zip-pool
  MAX_MINUTES: "60" # hard limit: the instance terminates after this
  # Run workflow inputs, passed to JMeter as -J (via env, never pasted into scripts).
  THREAD_COUNT: ${{ inputs.thread_count }}
  RAMP_UP_SECONDS: ${{ inputs.ramp_up_seconds }}
  TEST_DURATION_SECONDS: ${{ inputs.test_duration_seconds }}
  THINK_TIME_MIN_MS: ${{ inputs.think_time_min_ms }}
  THINK_TIME_MAX_MS: ${{ inputs.think_time_max_ms }}

jobs:
  run:
    # Only the default branch may start a run (add a branch policy on the GitHub environment as well, if you like).
    if: github.ref == 'refs/heads/main'
    runs-on: ubuntu-latest
    environment: <GITHUB_ENVIRONMENT> # must equal perf_test_github_environment in Terraform
    timeout-minutes: 75
    steps:
      - uses: actions/checkout@v4

      - uses: aws-actions/configure-aws-credentials@v4
        with:
          role-to-assume: ${{ vars.PERF_AWS_ROLE_ARN }}
          aws-region: ${{ vars.PERF_AWS_REGION }}

      # ------------------------------------------------------------------------------------------------------------
      - name: Show and check run settings
        env:
          BUCKET: ${{ vars.PERF_RESULTS_BUCKET }}
          LAUNCH_TEMPLATE_ID: ${{ vars.PERF_LAUNCH_TEMPLATE_ID }}
        run: |
          identity=$(aws sts get-caller-identity --query Arn --output text)

          echo "▶ [1/7] Performance test settings"
          echo "  Run script     : ${RUN_SCRIPT}"
          echo "  Test plan      : ${TEST_PLAN}"
          echo "  Config file    : ${CONFIG_FILE}"
          echo "  Test data      : ${TEST_DATA} + ${ZIP_POOL}/"
          echo "  Users          : ${THREAD_COUNT} (started over ${RAMP_UP_SECONDS}s)"
          echo "  Duration       : ${TEST_DURATION_SECONDS}s"
          echo "  Think time     : ${THINK_TIME_MIN_MS}-${THINK_TIME_MAX_MS} ms between requests"
          echo "  JMeter         : ${JMETER_VERSION} (heap ${JMETER_HEAP})"
          echo "  Time limit     : ${MAX_MINUTES} min (instance terminates itself after this)"
          echo "  Results bucket : s3://${BUCKET}"
          echo "  Launch template: ${LAUNCH_TEMPLATE_ID}"
          echo "  AWS identity   : ${identity}"

          # --- Every input must be a whole number ---
          for name in THREAD_COUNT RAMP_UP_SECONDS TEST_DURATION_SECONDS THINK_TIME_MIN_MS THINK_TIME_MAX_MS; do
            if [[ ! "${!name}" =~ ^(0|[1-9][0-9]{0,6})$ ]]; then # no leading zeros: bash would read 08 as octal
              echo "::error::${name} must be a whole number (got '${!name}')"
              exit 1
            fi
          done
          if (( THREAD_COUNT < 1 || TEST_DURATION_SECONDS < 1 )); then
            echo "::error::thread_count and test_duration_seconds must be at least 1"
            exit 1
          fi
          if (( THINK_TIME_MIN_MS > THINK_TIME_MAX_MS )); then
            echo "::error::think_time_min_ms (${THINK_TIME_MIN_MS}) is larger than think_time_max_ms (${THINK_TIME_MAX_MS})"
            exit 1
          fi

          # --- The test must fit inside the instance's time limit, leaving 10 min for setup and the report ---
          budget=$(( (MAX_MINUTES - 10) * 60 ))
          if (( RAMP_UP_SECONDS + TEST_DURATION_SECONDS > budget )); then
            echo "::error::ramp-up + duration ($(( RAMP_UP_SECONDS + TEST_DURATION_SECONDS ))s) is over the ${budget}s limit (MAX_MINUTES=${MAX_MINUTES})"
            exit 1
          fi
          echo "✔ Settings are valid."

      # ------------------------------------------------------------------------------------------------------------
      - name: Ensure JMeter is in S3
        env:
          BUCKET: ${{ vars.PERF_RESULTS_BUCKET }}
        run: |
          key="tools/apache-jmeter-${JMETER_VERSION}.tgz"
          file="jmeter/binaries/apache-jmeter-${JMETER_VERSION}.tgz"

          # --- Already in S3? Then nothing to do ---
          echo "▶ [2/7] Checking whether JMeter ${JMETER_VERSION} is already in s3://${BUCKET}/${key}"
          if aws s3api head-object --bucket "$BUCKET" --key "$key" >/dev/null 2>&1; then
            echo "✔ Found in S3 — skipping download. The instance will copy it from there."
            exit 0
          fi

          # --- Download from Apache: the CDN is fast but has current releases only; the archive has all, slowly ---
          echo "  Not in S3 yet (first run, or a new version). Downloading it once from Apache..."
          if ! curl -sSfL --max-time 300 -o jmeter.tgz "https://dlcdn.apache.org/${file}"; then
            echo "  CDN download failed — falling back to archive.apache.org (slower)"
            curl -sSfL --max-time 1800 -o jmeter.tgz "https://archive.apache.org/dist/${file}"
          fi
          echo "  Downloaded $(du -h jmeter.tgz | cut -f1)."

          # --- Verify the SHA-512 checksum published by Apache ---
          echo "  Verifying its SHA-512 checksum..."
          if ! sha=$(curl -sSfL "https://downloads.apache.org/${file}.sha512"); then
            sha=$(curl -sSfL "https://archive.apache.org/dist/${file}.sha512")
          fi
          echo "$(awk '{print $1}' <<<"$sha")  jmeter.tgz" | sha512sum -c -

          # --- Store it in S3 for this and every later run ---
          echo "  Checksum OK. Uploading to S3 so this and every later run can use it..."
          aws s3 cp --only-show-errors jmeter.tgz "s3://${BUCKET}/${key}"
          echo "✔ JMeter ${JMETER_VERSION} is now in S3."

      # ------------------------------------------------------------------------------------------------------------
      - name: Upload test inputs
        id: plan
        env:
          BUCKET: ${{ vars.PERF_RESULTS_BUCKET }}
        run: |
          run_id="$(date -u +%Y%m%dT%H%M%SZ)-${GITHUB_RUN_ID}"
          inputs_uri="s3://${BUCKET}/runs/${run_id}/inputs"

          files=("$RUN_SCRIPT" "$TEST_PLAN" "$CONFIG_FILE" "$TEST_DATA")

          echo "▶ [3/7] Uploading the run script, test plan, config, test data and zip-pool"
          echo "  Run ID: ${run_id} (all of this run's files go under s3://${BUCKET}/runs/${run_id}/)"

          # --- The script and everything it uses must exist; fail here rather than after the instance boots ---
          for f in "${files[@]}"; do
            if [[ ! -f "$f" ]]; then
              echo "::error::${f} is missing from the repo"
              exit 1
            fi
          done
          zip_count=$(find "$ZIP_POOL" -name '*.zip' 2>/dev/null | wc -l || true)
          if (( zip_count == 0 )); then
            echo "::error::No ZIPs found in ${ZIP_POOL}/"
            exit 1
          fi

          # --- Upload keeping each file's path inside perf-tests/: the run script refers to them by name ---
          for f in "${files[@]}"; do
            aws s3 cp --only-show-errors "$f" "${inputs_uri}/${f#perf-tests/}"
            echo "  ${f} → ${inputs_uri}/${f#perf-tests/}"
          done
          aws s3 cp --only-show-errors --recursive --exclude '*' --include '*.zip' \
            "$ZIP_POOL" "${inputs_uri}/${ZIP_POOL#perf-tests/}/"
          echo "  ${ZIP_POOL}/ → ${inputs_uri}/${ZIP_POOL#perf-tests/}/ (${zip_count} ZIPs)"
          echo "✔ Test inputs uploaded."

          echo "run_id=${run_id}" >> "$GITHUB_OUTPUT"

      # ------------------------------------------------------------------------------------------------------------
      - name: Launch load generator
        id: launch
        env:
          BUCKET: ${{ vars.PERF_RESULTS_BUCKET }}
          LAUNCH_TEMPLATE_ID: ${{ vars.PERF_LAUNCH_TEMPLATE_ID }}
          RUN_ID: ${{ steps.plan.outputs.run_id }}
        run: |
          echo "▶ [4/7] Launching the EC2 load generator"

          # --- Build the start-up script: this run's settings + perf-tests/run-on-instance.sh ---
          echo "  Building the instance start-up script: run settings + perf-tests/run-on-instance.sh"
          {
            echo '#!/bin/bash'
            printf 'BUCKET=%q RUN_ID=%q JMETER_VERSION=%q JMETER_HEAP=%q MAX_MINUTES=%q RUN_SCRIPT=%q\n' \
              "$BUCKET" "$RUN_ID" "$JMETER_VERSION" "$JMETER_HEAP" "$MAX_MINUTES" "${RUN_SCRIPT#perf-tests/}"
            printf 'THREAD_COUNT=%q RAMP_UP_SECONDS=%q TEST_DURATION_SECONDS=%q THINK_TIME_MIN_MS=%q THINK_TIME_MAX_MS=%q\n' \
              "$THREAD_COUNT" "$RAMP_UP_SECONDS" "$TEST_DURATION_SECONDS" "$THINK_TIME_MIN_MS" "$THINK_TIME_MAX_MS"
            tail -n +2 perf-tests/run-on-instance.sh
          } > user-data.sh

          # --- Look up the instance type, for the log ---
          instance_type=$(aws ec2 describe-launch-template-versions \
            --launch-template-id "$LAUNCH_TEMPLATE_ID" \
            --versions '$Latest' \
            --query 'LaunchTemplateVersions[0].LaunchTemplateData.InstanceType' \
            --output text)

          # --- Launch ---
          echo "  Starting a ${instance_type} instance from launch template ${LAUNCH_TEMPLATE_ID}..."
          tags="{Key=Name,Value=${NAME_PREFIX}-${RUN_ID}},{Key=Purpose,Value=perf-test},{Key=RunId,Value=${RUN_ID}}"
          instance_id=$(aws ec2 run-instances \
            --launch-template "LaunchTemplateId=${LAUNCH_TEMPLATE_ID},Version=\$Latest" \
            --user-data file://user-data.sh \
            --tag-specifications "ResourceType=instance,Tags=[${tags}]" \
            --query 'Instances[0].InstanceId' \
            --output text)

          echo "instance_id=${instance_id}" >> "$GITHUB_OUTPUT"

          echo "✔ Launched ${instance_id} (${NAME_PREFIX}-${RUN_ID})."
          echo "  On boot it will: install Java → copy JMeter + test plan from S3 → run the test →"
          echo "  upload results to S3 → write a DONE marker → terminate itself."
          echo "  Its own step-by-step log will be at s3://${BUCKET}/runs/${RUN_ID}/results/run.log"

      # ------------------------------------------------------------------------------------------------------------
      - name: Wait for the run to finish
        env:
          BUCKET: ${{ vars.PERF_RESULTS_BUCKET }}
          RUN_ID: ${{ steps.plan.outputs.run_id }}
          INSTANCE_ID: ${{ steps.launch.outputs.instance_id }}
        run: |
          done_key="runs/${RUN_ID}/DONE"
          deadline=$(( $(date +%s) + (MAX_MINUTES + 5) * 60 ))

          echo "▶ [5/7] Waiting for ${INSTANCE_ID} to finish"
          echo "  Checking every 30s for s3://${BUCKET}/${done_key} (gives up after $(( MAX_MINUTES + 5 )) min)"

          # --- Poll until the instance writes its DONE marker ---
          until aws s3api head-object --bucket "$BUCKET" --key "$done_key" >/dev/null 2>&1; do
            state=$(aws ec2 describe-instances \
              --instance-ids "$INSTANCE_ID" \
              --query 'Reservations[0].Instances[0].State.Name' \
              --output text)

            if [[ "$state" == "terminated" ]] || (( $(date +%s) > deadline )); then
              echo "::error::No DONE marker (instance state: ${state})"
              exit 1
            fi

            echo "  $(date -u +%H:%M:%S) not done yet — instance is ${state}"
            sleep 30
          done

          # --- Check the exit code the instance reported ---
          aws s3 cp --only-show-errors "s3://${BUCKET}/${done_key}" done.txt
          echo "  DONE marker found. Instance reported: $(cat done.txt)"

          if ! grep -qx 'exit=0' done.txt; then
            echo "::error::Test run failed — see runs/${RUN_ID}/results/run.log"
            exit 1
          fi
          echo "✔ Test finished successfully and results are in S3."

      # ------------------------------------------------------------------------------------------------------------
      - name: Summary
        if: always() && steps.plan.outputs.run_id != ''
        env:
          BUCKET: ${{ vars.PERF_RESULTS_BUCKET }}
          RUN_ID: ${{ steps.plan.outputs.run_id }}
        run: |
          uri="s3://${BUCKET}/runs/${RUN_ID}/results/"

          # One table row per request type, from the HTML report's statistics.json.
          row_filter='to_entries[] | .value | "| \(.transaction) | \(.sampleCount)
            | \(.errorPct | . * 100 | round / 100) | \(.meanResTime | round) | \(.pct1ResTime | round)
            | \(.pct3ResTime | round) | \(.throughput | . * 100 | round / 100) |" | gsub("\n *"; " ")'
          totals_filter='.Total | "\(.sampleCount) requests, \(.errorPct)% errors,
            mean \(.meanResTime | round) ms, p90 \(.pct1ResTime | round) ms" | gsub("\n *"; " ")'

          echo "▶ [6/7] Writing the results summary to this run's Summary page"

          # --- Fetch the statistics ---
          if aws s3 cp --only-show-errors "${uri}report/statistics.json" stats.json 2>/dev/null; then
            have_stats=true
          else
            have_stats=false
          fi

          # --- Summary page ---
          {
            echo "### Performance test ${RUN_ID}"
            echo "Results (private bucket): \`${uri}\`"
            echo
            echo "**HTML report:** download \`${uri}report.zip\`, unzip it, and open \`report/index.html\`."
            echo
            if $have_stats; then
              echo "| Request | Samples | Errors % | Mean ms | p90 ms | p99 ms | Req/s |"
              echo "|---|---|---|---|---|---|---|"
              jq -r "$row_filter" stats.json
            else
              echo "No statistics.json — the report wasn't generated."
            fi
          } >> "$GITHUB_STEP_SUMMARY"

          # --- Log ---
          if $have_stats; then
            echo "  Totals: $(jq -r "$totals_filter" stats.json)"
          else
            echo "  No statistics.json in S3 — the report wasn't generated."
          fi
          echo "✔ Full results: ${uri}"

      # ------------------------------------------------------------------------------------------------------------
      - name: Terminate instance (safety net)
        if: always() && steps.launch.outputs.instance_id != ''
        env:
          INSTANCE_ID: ${{ steps.launch.outputs.instance_id }}
        run: |
          echo "▶ [7/7] Making sure ${INSTANCE_ID} is gone (it normally terminates itself)"

          state=$(aws ec2 terminate-instances \
            --instance-ids "$INSTANCE_ID" \
            --query 'TerminatingInstances[0].CurrentState.Name' \
            --output text)

          echo "✔ Instance state: ${state}"
```

---

## Step 6 — Connect GitHub to the infrastructure

1. After the apply, read the outputs (`terragrunt output` in `infra/envs/<ENV>`):
   `perf_test_github_role_arn`, `perf_test_results_bucket`, `perf_test_launch_template_id`.
2. On the GitHub environment `<GITHUB_ENVIRONMENT>` (Settings → Environments), set **variables** (not secrets):

   | Variable | Value |
   |---|---|
   | `PERF_AWS_ROLE_ARN` | `perf_test_github_role_arn` |
   | `PERF_AWS_REGION` | the env's AWS region |
   | `PERF_RESULTS_BUCKET` | `perf_test_results_bucket` |
   | `PERF_LAUNCH_TEMPLATE_ID` | `perf_test_launch_template_id` |

   CLI: `gh variable set PERF_AWS_ROLE_ARN --env <GITHUB_ENVIRONMENT> --body <value>` (ask the user before changing
   GitHub settings).
3. Commit and push to the default branch (GitHub only offers "Run workflow" for workflows on it). **Apply Terraform
   before pushing** any workflow change that needs new permissions.

---

## Step 7 — Verify with a first run

Run **Actions → Performance Test → Run workflow** with the defaults (5 users, 10 s ramp-up, 120 s, 0–500 ms).

- [ ] Step [1/7] lists the settings and "✔ Settings are valid".
- [ ] Step [2/7] downloads JMeter once (seconds from Apache's CDN) and later runs say "Found in S3".
- [ ] Step [3/7] uploads the run script, plan, config, test data and the expected number of ZIPs.
- [ ] Step [4/7] prints the instance type from the launch template and launches an instance.
- [ ] The run's Summary page shows the results table.
- [ ] `s3://<bucket>/runs/<run id>/results/` holds `report.zip`, `report/`, `results.jtl`, `jmeter.log`, `run.log`;
      `DONE` says `exit=0`.
- [ ] `report.zip` unzips to a styled report (`report/index.html`).
- [ ] The instance is terminated (EC2 console, tag `Purpose=perf-test`).

---

## Gotchas found while building the original (handle them up front)

| Symptom | Cause | Fix (already in the code above) |
|---|---|---|
| `UnauthorizedOperation … explicit deny in a service control policy` on RunInstances | The account's SCP blocks the instance type | Pick an allowed type (dry-run check, Step 0.5) |
| HTML report fails, no `summary =` lines | `jmeter -p` *replaces* `jmeter.properties` | Use `-q` when calling JMeter directly. `run-dryrun.sh` uses `-p` but builds the report with a separate `jmeter -g`, which works |
| `index.html` opens unstyled after downloading from S3 | The S3 console downloads single files, not folders | `run-on-instance.sh` ships `results/report.zip` |
| Workflow says "No ZIPs found" | Root `.gitignore` ignores `*.zip` | `!zip-pool/**/*.zip` in `perf-tests/.gitignore` |
| Every request fails (401/403, "ZIP not found") | Only the plan was uploaded; it also reads `config/test-data.csv` and `zip-pool/` by relative path | Upload all inputs keeping their `perf-tests/` paths; run from inside the test folder |
| JMeter download takes ~7 min | `archive.apache.org` is slow | Workflow tries `dlcdn.apache.org` first, then caches in S3 |
| Launch template console shows an old default version | Terraform adds versions but doesn't move the default by itself | `update_default_version = true` |
| Perpetual tag diffs / apply fails removing tags | Cloud Custodian auto-tags resources; an SCP blocks removing them | `lifecycle { ignore_changes = [tags, tags_all] }` on every taggable resource |
| `08` in an input breaks the run | Bash reads leading zeros as octal | Input validation rejects leading zeros |
| Renaming the plan/config/test data/zip-pool breaks runs | They are also referenced inside `run-dryrun.sh`, the `.jmx` (`config/test-data.csv`) and the config (`zip.pool.path`) | Rename in the workflow env **and** in those files; only `RUN_SCRIPT` is workflow-only |
| Load values in `config.dryrun.properties` have no effect in CI | The 5 workflow inputs always pass `-J`, which overrides the config | Change the workflow input defaults instead |
