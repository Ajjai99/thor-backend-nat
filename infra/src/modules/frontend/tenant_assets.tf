# Tenant login-page assets (logos), served by this distribution under /tenant-assets/* so they come
# from the same host as the SPA's login page. Lives in this module because the bucket policy needs the
# distribution's ARN and the distribution needs the bucket as an origin.
#
# Thor.Api owns the content: a tenant admin uploads to pending/<tenantId>/<uploadId> with a presigned
# PUT, and Thor.Api validates the bytes before copying them to tenant-assets/logos/. Only the
# tenant-assets/ prefix is readable through CloudFront, so nothing unvalidated is ever served.

locals {
  # Globally unique — bucket names share a namespace across all of AWS. thor-<env>-* is the prefix the
  # deploy role's grants and the permissions boundary are scoped to.
  tenant_assets_bucket_name = "thor-${var.environment}-tenant-assets-${data.aws_caller_identity.current.account_id}"
  tenant_assets_origin_id   = "tenant-assets"

  # Browsers PUT presigned uploads straight to S3 from the tenant's SPA host.
  tenant_assets_cors_origins = local.use_custom_domain ? ["https://*.${var.domain_name}"] : ["https://${aws_cloudfront_distribution.thor-fe-cdn.domain_name}"]
}

resource "aws_s3_bucket" "tenant_assets" {
  bucket = local.tenant_assets_bucket_name

  tags = merge(var.tags, {
    Name = local.tenant_assets_bucket_name
  })

  # Cloud Custodian auto-tags this after creation and an SCP blocks removing it — ignore tags to avoid fighting it.
  lifecycle {
    ignore_changes = [tags, tags_all]
  }
}

resource "aws_s3_bucket_public_access_block" "tenant_assets" {
  bucket = aws_s3_bucket.tenant_assets.id

  block_public_acls       = true
  block_public_policy     = true
  ignore_public_acls      = true
  restrict_public_buckets = true
}

resource "aws_s3_bucket_ownership_controls" "tenant_assets" {
  bucket = aws_s3_bucket.tenant_assets.id

  rule {
    object_ownership = "BucketOwnerEnforced"
  }
}

resource "aws_s3_bucket_server_side_encryption_configuration" "tenant_assets" {
  bucket = aws_s3_bucket.tenant_assets.id

  rule {
    apply_server_side_encryption_by_default {
      sse_algorithm = "AES256"
    }
  }
}

resource "aws_s3_bucket_cors_configuration" "tenant_assets" {
  bucket = aws_s3_bucket.tenant_assets.id

  cors_rule {
    allowed_methods = ["PUT"]
    allowed_origins = local.tenant_assets_cors_origins
    allowed_headers = ["Content-Type"]
    max_age_seconds = 3000
  }
}

# An upload that's never published (abandoned, or rejected by Thor.Api's checks) is never served and
# is cleaned up here. A presigned PUT can't cap object size, so this also bounds what an oversized
# upload can cost.
resource "aws_s3_bucket_lifecycle_configuration" "tenant_assets" {
  bucket = aws_s3_bucket.tenant_assets.id

  rule {
    id     = "expire-pending-uploads"
    status = "Enabled"

    filter {
      prefix = "pending/"
    }

    expiration {
      days = 1
    }

    abort_incomplete_multipart_upload {
      days_after_initiation = 1
    }
  }
}

data "aws_iam_policy_document" "tenant_assets" {
  statement {
    sid       = "AllowCloudFrontReadOfPublishedAssets"
    actions   = ["s3:GetObject"]
    resources = ["${aws_s3_bucket.tenant_assets.arn}/tenant-assets/*"]

    principals {
      type        = "Service"
      identifiers = ["cloudfront.amazonaws.com"]
    }

    condition {
      test     = "StringEquals"
      variable = "AWS:SourceArn"
      values   = [aws_cloudfront_distribution.thor-fe-cdn.arn]
    }
  }

  statement {
    sid     = "DenyInsecureTransport"
    effect  = "Deny"
    actions = ["s3:*"]

    principals {
      type        = "*"
      identifiers = ["*"]
    }

    resources = [
      aws_s3_bucket.tenant_assets.arn,
      "${aws_s3_bucket.tenant_assets.arn}/*",
    ]

    condition {
      test     = "Bool"
      variable = "aws:SecureTransport"
      values   = ["false"]
    }
  }
}

resource "aws_s3_bucket_policy" "tenant_assets" {
  bucket = aws_s3_bucket.tenant_assets.id
  policy = data.aws_iam_policy_document.tenant_assets.json

  depends_on = [aws_s3_bucket_public_access_block.tenant_assets]
}
