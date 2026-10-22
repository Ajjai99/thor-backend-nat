# Performance Testing

Load-test Thor with JMeter from a GitHub workflow. No AWS access is needed to run a test: the workflow starts a
temporary EC2 instance, runs the test on it, saves the results to S3, and deletes the instance.

## 1. What it's for

We need a simple way to check how Thor handles heavy load, like millions of records coming in through the
connector. This pipeline lets the QA team start a performance test from GitHub in one click and see the results in
S3. It shows how fast Thor responds, where it slows down or breaks, and whether it's ready for production data.

## 2. How it works

```
GitHub: Actions → Performance Test → Run workflow (load settings as inputs)
  │
  ├─ [1/7] Show and check the run settings
  ├─ [2/7] Make sure JMeter is in S3 (downloaded from Apache only the first time)
  ├─ [3/7] Upload the test plan (.jmx) and config.properties to S3
  ├─ [4/7] Launch an EC2 instance from the launch template
  │          │
  │          └─ On the instance (run-on-instance.sh):
  │               [1/6] start, set a hard time limit
  │               [2/6] install Java (Amazon Corretto 21)
  │               [3/6] copy JMeter from S3
  │               [4/6] copy the test plan and config.properties from S3
  │               [5/6] run JMeter in non-GUI mode
  │               [6/6] upload results to S3, write a DONE marker, terminate itself
  │
  ├─ [5/7] Wait for the DONE marker in S3
  ├─ [6/7] Post a summary table on the GitHub run
  └─ [7/7] Terminate the instance (safety net, always runs)
```

The JMeter command run on the instance:

```bash
jmeter -n -t plan.jmx -q config.properties \
  -Jthread.count=<input> -Jramp.up.seconds=<input> -Jtest.duration.seconds=<input> \
  -Jthink.time.min.ms=<input> -Jthink.time.max.ms=<input> \
  -l results/results.jtl -j results/jmeter.log -e -o results/report
```

`-q` adds `config.properties` on top of JMeter's own settings. (`-p` would replace them and break the HTML
report.) The `-J` values are applied last, so they override the same keys in `config.properties`.

## 3. How to run a test

1. In GitHub, open **Actions → Performance Test**.
2. Click **Run workflow** and keep the branch as **`main`** (the workflow only runs from `main`).
3. Set the load, or keep the defaults:

   | Input | Default | Meaning |
   |---|---|---|
   | `thread_count` | 5 | Number of concurrent users |
   | `ramp_up_seconds` | 10 | Seconds to start all users |
   | `test_duration_seconds` | 120 | How long the test runs |
   | `think_time_min_ms` | 0 | Shortest pause between a user's requests |
   | `think_time_max_ms` | 500 | Longest pause between a user's requests |

4. Click **Run workflow**. Each step prints what it's doing in the log.
5. When it finishes, open the run's **Summary** page for the results table.

Notes:
- Inputs must be whole numbers, the minimum think time can't be more than the maximum, and ramp-up + duration
  must fit within the time limit (`MAX_MINUTES`, 60 by default, minus 10 minutes for setup and the report).
- Only one test runs at a time. A second run waits for the first to finish.

## 4. Results

Every run gets its own folder in the results bucket:

```
s3://<results bucket>/runs/<run id>/
├── plan.jmx                 the test plan that was run
├── config.properties        the config that was used
├── DONE                     exit=0 if the run succeeded
└── results/
    ├── report/index.html    JMeter's HTML dashboard (open this)
    ├── report/statistics.json
    ├── results.jtl          raw result of every request
    ├── jmeter.log           JMeter's log
    └── run.log              step-by-step log from the instance
```

- The **run id** is printed in step [3/7] of the workflow log and on the Summary page, for example
  `20261020T093140Z-38880296084`.
- Results are **deleted automatically after 30 days**.
- The report is kept **only in S3**, never attached to the GitHub run, because this repository is public.

To open the HTML report, download the `report` folder and open `index.html` in a browser:

```bash
aws s3 cp --recursive s3://<results bucket>/runs/<run id>/results/report ./report
open report/index.html        # macOS; on Windows, double-click index.html
```

## 5. Files in this folder

| File | Purpose |
|---|---|
| `dummy-public-data.jmx` | Placeholder test plan. It calls JSONPlaceholder, a free public fake API, until QA's connector test plan is added. |
| `config/config.properties` | Target system (protocol, host, port), HTTP timeouts and load defaults, read by the test plan with `${__P(key)}`. |
| `run-on-instance.sh` | Runs on the EC2 instance at boot: installs Java, gets JMeter and the test files from S3, runs the test, uploads results, terminates the instance. |

The workflow itself is `.github/workflows/performance-test.yml`.

## 6. Changing things

| To change | Edit | Notes |
|---|---|---|
| The test plan | `TEST_PLAN` in the workflow | Put the `.jmx` in `perf-tests/`. |
| Target system or timeouts | `config/config.properties` | No secrets: the repo is public. Pass secrets from GitHub secrets as `-J`. |
| Default load values | The `inputs` defaults in the workflow | The inputs always override the load values in `config.properties`. |
| JMeter version or heap | `JMETER_VERSION` / `JMETER_HEAP` in the workflow | A new version is downloaded to S3 on the next run. |
| Time limit | `MAX_MINUTES` in the workflow | The instance terminates itself after this. Keep it below the job's `timeout-minutes`. |
| Instance type or disk size | `infra/poc/perf-test/variables.tf`, then `terraform apply` | See below. Infrastructure changes go through Terraform, not the workflow. |

## 7. Infrastructure

Terraform in `infra/poc/perf-test` creates the pieces the workflow uses. Thor's existing networking (VPC, private
subnets, NAT gateway, DNS Firewall) is used as is. Names follow `thor-<env>-perf-*`.

**S3 results bucket:** `thor-<env>-perf-results-<account id>`
- Holds JMeter (`tools/`, kept) and each run's files (`runs/`, deleted after 30 days).
- Encrypted, all public access blocked, HTTPS-only access.

**Launch template:** `thor-<env>-perf-jmeter`
- Instance type `t3.xlarge` (4 vCPU, 16 GiB) and a 50 GB encrypted gp3 disk.
  - The QA target is `c5n.2xlarge` (25 Gbps), later `c5n.4xlarge`. The sandbox account's organization policy
    blocks c5n, so t3.xlarge is used until that's allowed.
  - Change `default_instance_type` / `root_volume_gb` and run `terraform apply`. Each change becomes the
    template's default version.
- Amazon Linux 2023, private subnet, no public IP, IMDSv2 only.
- **Terminates on shutdown,** so the instance deletes itself when the test ends.
- Security group: outbound HTTPS only, nothing inbound.

**Instance role:** `thor-<env>-perf-jmeter`, attached to the launch template through an instance profile
- Can **read** only `tools/*`, `runs/*/plan.jmx` and `runs/*/config.properties` in the results bucket.
- Can **write** only `runs/*/results/*` and `runs/*/DONE`.
- No other AWS permissions.

**GitHub environment variables:** set on the `prod` environment, from `terraform output`:

| Variable | Value |
|---|---|
| `PERF_AWS_ROLE_ARN` | IAM role the workflow logs in with (GitHub OIDC, no stored keys) |
| `PERF_AWS_REGION` | AWS region |
| `PERF_RESULTS_BUCKET` | Results bucket name |
| `PERF_LAUNCH_TEMPLATE_ID` | Launch template ID |
