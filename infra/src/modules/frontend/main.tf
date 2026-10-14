terraform {
  required_version = ">= 1.15"

  required_providers {
    aws = {
      source  = "hashicorp/aws"
      version = "~> 6.0"
    }
  }
}

data "aws_caller_identity" "current" {}

locals {
  name_prefix = "thor-${var.environment}-frontend"
  # Globally unique — bucket names share a namespace across all of AWS, not just this account.
  bucket_name = "thor-frontend-${var.environment}-${data.aws_caller_identity.current.account_id}"

  use_custom_domain = var.domain_name != ""
}

# Private bucket — no website hosting, no public ACLs. CloudFront reaches it through the origin access control below, never directly.
resource "aws_s3_bucket" "thor-fe-bucket" {
  bucket = local.bucket_name

  tags = merge(var.tags, {
    Name = local.bucket_name
  })

  # Cloud Custodian auto-tags this after creation and an SCP blocks removing it — ignore tags to avoid fighting it.
  lifecycle {
    ignore_changes = [tags, tags_all]
  }
}

resource "aws_s3_bucket_public_access_block" "thor-fe-bucket-pab" {
  bucket = aws_s3_bucket.thor-fe-bucket.id

  block_public_acls       = true
  block_public_policy     = true
  ignore_public_acls      = true
  restrict_public_buckets = true
}

# BucketOwnerEnforced — object ACLs are disabled entirely, matching an OAC-fronted bucket where only the bucket policy (not ACLs) grants access.
resource "aws_s3_bucket_ownership_controls" "thor-fe-bucket-ownership" {
  bucket = aws_s3_bucket.thor-fe-bucket.id

  rule {
    object_ownership = "BucketOwnerEnforced"
  }
}

resource "aws_cloudfront_origin_access_control" "thor-fe-oac" {
  name                              = local.name_prefix
  origin_access_control_origin_type = "s3"
  signing_behavior                  = "always"
  signing_protocol                  = "sigv4"
}

resource "aws_cloudfront_function" "spa_rewrite" {
  name    = "${local.name_prefix}-spa-rewrite"
  runtime = "cloudfront-js-2.0"
  comment = "Serves index.html for SPA client-side routes"
  publish = true
  code    = file("${path.module}/functions/spa_rewrite.js")
}

resource "aws_cloudfront_distribution" "thor-fe-cdn" {
  enabled             = true
  comment             = local.name_prefix
  default_root_object = "index.html"
  price_class         = var.price_class
  web_acl_id          = aws_wafv2_web_acl.thor-fe-waf.arn
  # The wildcard serves every tenant subdomain (<tenant>.<domain_name>), whose provisioned CNAME
  # points here; CloudFront rejects any hostname not listed. The ACM cert already covers it.
  aliases = local.use_custom_domain ? [var.domain_name, "*.${var.domain_name}"] : []

  origin {
    domain_name              = aws_s3_bucket.thor-fe-bucket.bucket_regional_domain_name
    origin_id                = local.bucket_name
    origin_access_control_id = aws_cloudfront_origin_access_control.thor-fe-oac.id
  }

  # Tenant logos (see tenant_assets.tf). The OAC isn't bucket-specific, so the same one signs both origins.
  origin {
    domain_name              = aws_s3_bucket.tenant_assets.bucket_regional_domain_name
    origin_id                = local.tenant_assets_origin_id
    origin_access_control_id = aws_cloudfront_origin_access_control.thor-fe-oac.id
  }

  # The API (see api_origin.tf). Stage isn't $default, so its name is a real path segment.
  dynamic "origin" {
    for_each = local.api_origin_enabled ? [1] : []

    content {
      domain_name = var.api_origin_domain_name
      origin_id   = local.api_origin_id
      origin_path = var.api_origin_path

      custom_origin_config {
        http_port              = 80
        https_port             = 443
        origin_protocol_policy = "https-only"
        origin_ssl_protocols   = ["TLSv1.2"]
      }
    }
  }

  dynamic "ordered_cache_behavior" {
    for_each = local.api_origin_enabled ? [1] : []

    content {
      path_pattern     = "/api/*"
      target_origin_id = local.api_origin_id
      # https-only, not redirect-to-https: a redirected POST/PUT loses its body, and an http API
      # call is a client bug to surface, not paper over.
      viewer_protocol_policy = "https-only"
      # The API takes every method (integration.tf uses ANY); CloudFront only offers this set with OPTIONS in it.
      allowed_methods = ["GET", "HEAD", "OPTIONS", "PUT", "POST", "PATCH", "DELETE"]
      cached_methods  = ["GET", "HEAD"]
      compress        = true

      cache_policy_id          = data.aws_cloudfront_cache_policy.caching_disabled.id
      origin_request_policy_id = data.aws_cloudfront_origin_request_policy.all_viewer_except_host.id

      function_association {
        event_type   = "viewer-request"
        function_arn = aws_cloudfront_function.api_strip_prefix[0].arn
      }
    }
  }

  ordered_cache_behavior {
    path_pattern           = "/tenant-assets/*"
    target_origin_id       = local.tenant_assets_origin_id
    viewer_protocol_policy = "redirect-to-https"
    allowed_methods        = ["GET", "HEAD"]
    cached_methods         = ["GET", "HEAD"]
    compress               = true

    # AWS-managed "CachingOptimized" — logo keys are never overwritten (a new logo gets a new key).
    cache_policy_id = "658327ea-f89d-4fab-a63d-7e88639e58f6"
    # AWS-managed "SecurityHeadersPolicy" — chiefly X-Content-Type-Options: nosniff, so a browser
    # never second-guesses the image Content-Type Thor.Api set on a tenant-supplied file.
    response_headers_policy_id = "67f7725c-6f97-4210-82d7-5512b31e9d03"
  }

  default_cache_behavior {
    target_origin_id       = local.bucket_name
    viewer_protocol_policy = "redirect-to-https"
    allowed_methods        = ["GET", "HEAD"]
    cached_methods         = ["GET", "HEAD"]
    compress               = true

    # AWS-managed "CachingOptimized" policy — long-lived caching for a static SPA build, no per-behavior TTL tuning needed here.
    cache_policy_id = "658327ea-f89d-4fab-a63d-7e88639e58f6"

    # SPA client-side routing (a React Router route, for example) gets index.html. Done per
    # behavior, not with custom_error_response: those apply distribution-wide, so they'd turn the
    # API's own 403/404s on /api/* into a 200 index.html.
    function_association {
      event_type   = "viewer-request"
      function_arn = aws_cloudfront_function.spa_rewrite.arn
    }
  }

  restrictions {
    geo_restriction {
      restriction_type = "none"
    }
  }

  # Falls back to the default *.cloudfront.net certificate when domain_name
  # isn't set — cloudfront_default_certificate and the acm_* fields are
  # mutually exclusive as far as CloudFront's API is concerned, so exactly
  # one side of this is non-null at a time, never both.
  viewer_certificate {
    cloudfront_default_certificate = local.use_custom_domain ? null : true
    acm_certificate_arn            = local.use_custom_domain ? var.acm_certificate_arn : null
    ssl_support_method             = local.use_custom_domain ? "sni-only" : null
    minimum_protocol_version       = local.use_custom_domain ? "TLSv1.2_2021" : null
  }

  tags = merge(var.tags, {
    Name = local.name_prefix
  })

  # Cloud Custodian auto-tags this after creation and an SCP blocks removing it — ignore tags to avoid fighting it.
  lifecycle {
    ignore_changes = [tags, tags_all]
  }
}

# Only created once domain_name is set — CloudFront's own hosted_zone_id
# is a fixed, well-known constant for every distribution
# globally, not something looked up per-region.
resource "aws_route53_record" "alias_a" {
  count = local.use_custom_domain ? 1 : 0

  zone_id = var.zone_id
  name    = var.domain_name
  type    = "A"

  alias {
    name                   = aws_cloudfront_distribution.thor-fe-cdn.domain_name
    zone_id                = aws_cloudfront_distribution.thor-fe-cdn.hosted_zone_id
    evaluate_target_health = false
  }
}

data "aws_iam_policy_document" "cloudfront_access" {
  statement {
    sid       = "AllowCloudFrontServicePrincipalReadOnly"
    actions   = ["s3:GetObject"]
    resources = ["${aws_s3_bucket.thor-fe-bucket.arn}/*"]

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
}

resource "aws_s3_bucket_policy" "thor-fe-bucket-policy" {
  bucket = aws_s3_bucket.thor-fe-bucket.id
  policy = data.aws_iam_policy_document.cloudfront_access.json
}
