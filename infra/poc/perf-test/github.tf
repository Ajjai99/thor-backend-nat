# GitHub Actions → AWS: the OIDC provider and the one role performance-test.yml assumes.
#
# The OIDC provider is account-wide (one per account for token.actions.githubusercontent.com) and other roles in
# this account already trust it by ARN (e.g. thor-shared-role). Before destroying this POC, keep it:
#   terraform state rm aws_iam_openid_connect_provider.github

# Adopts the provider if it already exists (e.g. after a teardown that kept it); a no-op once it's in state.
import {
  to = aws_iam_openid_connect_provider.github
  id = "arn:aws:iam::${local.account_id}:oidc-provider/token.actions.githubusercontent.com"
}

resource "aws_iam_openid_connect_provider" "github" {
  url            = "https://token.actions.githubusercontent.com"
  client_id_list = ["sts.amazonaws.com"]

  # Cloud Custodian auto-tags this after creation and an SCP blocks removing it — ignore tags to avoid fighting it.
  lifecycle {
    ignore_changes = [tags, tags_all]
  }
}

resource "aws_iam_role" "github" {
  name = "${local.name}-github"

  # Only the workflow running in this repo's `<var.environment>` GitHub environment. The subject is GitHub's
  # immutable form (owner and repo ids), so a renamed or re-created repo of the same name can't assume this.
  assume_role_policy = jsonencode({
    Version = "2012-10-17"
    Statement = [{
      Effect    = "Allow"
      Principal = { Federated = aws_iam_openid_connect_provider.github.arn }
      Action    = "sts:AssumeRoleWithWebIdentity"
      Condition = {
        StringEquals = {
          "token.actions.githubusercontent.com:aud" = "sts.amazonaws.com"
          "token.actions.githubusercontent.com:sub" = "${var.github_sub_prefix}:environment:${var.environment}"
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
        Resource = "arn:aws:ec2:${var.aws_region}:${local.account_id}:instance/*"
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
          "arn:aws:ec2:${var.aws_region}:${local.account_id}:subnet/${module.network.private_subnet_ids[0]}",
          aws_security_group.jmeter.arn,
          "arn:aws:ec2:${var.aws_region}:${local.account_id}:network-interface/*",
          "arn:aws:ec2:${var.aws_region}:${local.account_id}:volume/*",
        ]
      },
      {
        Sid       = "TagAtLaunch"
        Effect    = "Allow"
        Action    = "ec2:CreateTags"
        Resource  = "arn:aws:ec2:${var.aws_region}:${local.account_id}:*/*"
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
        Resource  = "arn:aws:ec2:${var.aws_region}:${local.account_id}:instance/*"
        Condition = { StringEquals = { "aws:ResourceTag/Purpose" = "perf-test" } }
      },
      {
        Sid      = "Describe"
        Effect   = "Allow"
        Action   = ["ec2:DescribeInstances", "ec2:DescribeInstanceStatus"]
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
