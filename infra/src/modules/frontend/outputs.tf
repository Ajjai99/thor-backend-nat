output "bucket_name" {
  value = aws_s3_bucket.thor-fe-bucket.id
}

output "bucket_arn" {
  value = aws_s3_bucket.thor-fe-bucket.arn
}

output "distribution_id" {
  value = aws_cloudfront_distribution.thor-fe-cdn.id
}

output "distribution_arn" {
  value = aws_cloudfront_distribution.thor-fe-cdn.arn
}

output "tenant_assets_bucket_name" {
  description = "Passed to thor-api as THOR_TENANT_ASSETS_BUCKET (tenant logo uploads, see Thor.Api's TenantBrandingService)"
  value       = aws_s3_bucket.tenant_assets.bucket
}

output "distribution_domain_name" {
  value = aws_cloudfront_distribution.thor-fe-cdn.domain_name
}
