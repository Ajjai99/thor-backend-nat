# Pinned to us-east-1 for the same reason as modules/frontend's: a CLOUDFRONT-scoped WAF ACL is global
# once created, but only the us-east-1 endpoint will create one — every other region rejects the scope
# ("WAFInvalidParameterException: The scope is not valid., parameter: CLOUDFRONT"). Note this is the
# only resource in this module that leaves the stack's region; the API itself and its NLB integration
# stay regional, and the distribution in cdn.tf is global in its own right.
#
# Its visibility_config metrics therefore land in us-east-1 CloudWatch, not with the rest of this
# environment's — including the rate-limit metric, which is the one most likely to be alarmed on.
resource "aws_wafv2_web_acl" "api" {
  region = var.global_region

  name        = "${local.cdn_name_prefix}-waf"
  description = "WAF for the thor-${var.environment} API CloudFront distribution"
  scope       = "CLOUDFRONT"

  default_action {
    allow {}
  }

  # AWS-managed core rule set — common exploits (XSS, path traversal, etc.). Its
  # SizeRestrictions_BODY (blocks bodies over 8KB) only counts here: body-size-limit below
  # re-applies the same 8KB block with one exception, rather than dropping the whole group.
  rule {
    name     = "AWSManagedRulesCommonRuleSet"
    priority = 1

    override_action {
      none {}
    }

    statement {
      managed_rule_group_statement {
        name        = "AWSManagedRulesCommonRuleSet"
        vendor_name = "AWS"

        rule_action_override {
          name = "SizeRestrictions_BODY"

          action_to_use {
            count {}
          }
        }
      }
    }

    visibility_config {
      cloudwatch_metrics_enabled = true
      metric_name                = "${local.cdn_name_prefix}-common-rule-set"
      sampled_requests_enabled   = true
    }
  }

  # AWS-managed known-bad-inputs rule set — request patterns already known to be malicious.
  rule {
    name     = "AWSManagedRulesKnownBadInputsRuleSet"
    priority = 2

    override_action {
      none {}
    }

    statement {
      managed_rule_group_statement {
        name        = "AWSManagedRulesKnownBadInputsRuleSet"
        vendor_name = "AWS"
      }
    }

    visibility_config {
      cloudwatch_metrics_enabled = true
      metric_name                = "${local.cdn_name_prefix}-known-bad-inputs"
      sampled_requests_enabled   = true
    }
  }

  # Blocks a single IP sending more than var.cdn_waf_rate_limit requests in a rolling 5-minute
  # window. Tunable separately from the frontend's — an API's normal per-client call rate looks
  # nothing like a browser fetching a static bundle.
  rule {
    name     = "rate-limit-per-ip"
    priority = 3

    action {
      block {}
    }

    statement {
      rate_based_statement {
        limit              = var.cdn_waf_rate_limit
        aggregate_key_type = "IP"
      }
    }

    visibility_config {
      cloudwatch_metrics_enabled = true
      metric_name                = "${local.cdn_name_prefix}-rate-limit"
      sampled_requests_enabled   = true
    }
  }

  # SizeRestrictions_BODY's 8KB block, minus IdP create/update: those carry SAML metadata inline,
  # and Entra/ADFS metadata routinely exceeds 8KB. Thor.Api caps those two actions itself
  # (RequestSizeLimit), and every other rule-set check still applies to them. A body past WAF's
  # inspection limit counts as oversize (MATCH), so it can't slip under the size check.
  rule {
    name     = "body-size-limit"
    priority = 4

    action {
      block {}
    }

    statement {
      and_statement {
        statement {
          size_constraint_statement {
            comparison_operator = "GT"
            size                = 8192

            field_to_match {
              body {
                oversize_handling = "MATCH"
              }
            }

            text_transformation {
              priority = 0
              type     = "NONE"
            }
          }
        }

        statement {
          not_statement {
            statement {
              and_statement {
                statement {
                  regex_match_statement {
                    regex_string = "^(POST|PUT)$"

                    field_to_match {
                      method {}
                    }

                    text_transformation {
                      priority = 0
                      type     = "NONE"
                    }
                  }
                }

                statement {
                  regex_match_statement {
                    regex_string = "^/v[0-9]+/identity-providers(/[^/]+)?$"

                    field_to_match {
                      uri_path {}
                    }

                    text_transformation {
                      priority = 0
                      type     = "NONE"
                    }
                  }
                }
              }
            }
          }
        }
      }
    }

    visibility_config {
      cloudwatch_metrics_enabled = true
      metric_name                = "${local.cdn_name_prefix}-body-size-limit"
      sampled_requests_enabled   = true
    }
  }

  visibility_config {
    cloudwatch_metrics_enabled = true
    metric_name                = "${local.cdn_name_prefix}-waf"
    sampled_requests_enabled   = true
  }

  tags = merge(var.tags, {
    Name = "${local.cdn_name_prefix}-waf"
  })

  # Cloud Custodian auto-tags this post-creation and an SCP blocks removal — ignore tags here.
  lifecycle {
    ignore_changes = [tags, tags_all]
  }
}
