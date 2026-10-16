# Set these as variables on the repo's `prod` GitHub environment.

output "PERF_AWS_ROLE_ARN" {
  value = aws_iam_role.github.arn
}

output "PERF_AWS_REGION" {
  value = var.aws_region
}

output "PERF_RESULTS_BUCKET" {
  value = aws_s3_bucket.results.bucket
}

output "PERF_LAUNCH_TEMPLATE_ID" {
  value = aws_launch_template.jmeter.id
}
