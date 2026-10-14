# The API, served same-origin: the SPA calls <tenant>.<domain>/api/* on its own host, which this
# distribution forwards to API Gateway. Same-origin means no CORS at all — the API grants no
# cross-origin access (modules/api_gateway sets no cors_configuration), and browsers send no
# preflight, so the authorizer never sees an unauthenticated OPTIONS.
locals {
  api_origin_enabled = var.api_origin_domain_name != ""
  api_origin_id      = "apigw"
}

# Same managed pair as modules/api_gateway's own distribution, looked up by name so a typo fails at
# plan time: no caching (responses are per-caller authorized), and every viewer header except Host
# forwarded — Authorization included; execute-api routes by its own hostname and 403s the alias.
data "aws_cloudfront_cache_policy" "caching_disabled" {
  name = "Managed-CachingDisabled"
}

data "aws_cloudfront_origin_request_policy" "all_viewer_except_host" {
  name = "Managed-AllViewerExceptHostHeader"
}

# CloudFront forwards the viewer path as-is, but thor.api's routes have no /api prefix.
resource "aws_cloudfront_function" "api_strip_prefix" {
  count = local.api_origin_enabled ? 1 : 0

  name    = "${local.name_prefix}-api-strip-prefix"
  runtime = "cloudfront-js-2.0"
  comment = "Strips the leading /api before forwarding to API Gateway"
  publish = true
  code    = file("${path.module}/functions/api_strip_prefix.js")
}
