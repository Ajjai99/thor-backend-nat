# Performance-test POC

A JMeter load generator on EC2, run entirely from GitHub (`.github/workflows/performance-test.yml`) for a team with
read-only AWS access. Nobody logs in to the instance.

```
Run workflow (manual, no inputs)
  → JMeter tarball to S3 (first run only) → test plan to S3 → launch EC2 from the launch template
  → instance: dnf Java, JMeter + plan from S3, run, results to S3, write DONE, terminate itself
  → workflow: wait for DONE, write a summary table, terminate the instance (safety net)
```

Results: `s3://thor-prod-perf-results-<account>/runs/<run id>/results/` (`report/index.html`, `results.jtl`,
`jmeter.log`, `run.log`), deleted after 30 days. Not attached to the GitHub run — the repo is public.

## Setup

```bash
cd infra/poc/perf-test
terraform init && terraform apply
terraform output     # four values for the GitHub environment
```

In GitHub: use environment **`prod`** (runs only from `main`; add required reviewers so only approved people can start one) and
set the four outputs as its variables. The workflow must be on the default branch to show "Run workflow".

## Teardown

The GitHub OIDC provider is account-wide and other roles trust it, so keep it out of the destroy:

```bash
terraform state rm aws_iam_openid_connect_provider.github
terraform destroy
```
