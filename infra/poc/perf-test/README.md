# Performance-test infrastructure (POC)

Terraform for the JMeter performance-test pipeline. How the pipeline works and how to run a test:
[`perf-tests/README.md`](../../../perf-tests/README.md).

Creates (`var.environment = "prod"` → names `thor-prod-perf-*`): the S3 results bucket, the EC2 launch template
(t3.xlarge, 50 GB gp3), the instance role, and the GitHub OIDC provider + role the workflow logs in with.
In this POC it also builds its own VPC from the network module; in a real environment, use Thor's networking.

## Apply

```bash
cd infra/poc/perf-test
terraform init && terraform apply
terraform output     # set these four as variables on the GitHub `prod` environment
```

Apply before pushing a workflow change that needs new permissions.

## Teardown

The GitHub OIDC provider is account-wide and other roles trust it, so keep it out of the destroy:

```bash
terraform state rm aws_iam_openid_connect_provider.github
terraform destroy
```
