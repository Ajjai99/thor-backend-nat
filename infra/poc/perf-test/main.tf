# Throwaway POC: a JMeter load-generator EC2 instance driven entirely by a GitHub workflow
# (.github/workflows/performance-test.yml), for a team with read-only AWS access. The workflow launches one instance
# per run from the launch template below; the instance runs the test plan, writes results to S3, and terminates
# itself. Local state, no Terragrunt.

terraform {
  required_version = ">= 1.15"

  required_providers {
    aws = {
      source  = "hashicorp/aws"
      version = "~> 6.0"
    }
  }
}

provider "aws" {
  region = var.aws_region

  default_tags {
    tags = {
      Project     = "thor-platform"
      Environment = var.environment
      ManagedBy   = "terraform-poc-perf-test"
    }
  }
}

data "aws_caller_identity" "current" {}

locals {
  name       = "thor-${var.environment}-perf"
  account_id = data.aws_caller_identity.current.account_id
}

# Same network module as the real environments: private subnets, single NAT, DNS Firewall in ALERT.
module "network" {
  source = "../../src/modules/network"

  environment          = "${var.environment}-perf" # → thor-<env>-perf-*, never the real thor-<env>-* names
  vpc_cidr             = "10.98.0.0/16"
  az_count             = 2
  private_subnet_cidrs = ["10.98.10.0/24", "10.98.11.0/24"]
  public_subnet_cidrs  = ["10.98.0.0/24", "10.98.1.0/24"]
  nat_gateway_mode     = "single"

  # Each interface endpoint costs money while the POC is up; S3 and the dnf repos are reached through the NAT.
  enable_vpc_endpoints = false

  dns_firewall_default_action = "ALERT"
  egress_allowed_domains      = var.egress_allowed_domains
}

# --- results bucket ----------------------------------------------------------------------------------------------
# tools/<jmeter tarball>    uploaded once by the workflow (the instance never downloads from Apache)
# runs/<run id>/plan.jmx    uploaded by the workflow
# runs/<run id>/results/    written by the instance: results.jtl, report/, jmeter.log, run.log
# runs/<run id>/DONE        written by the instance last: "exit=<code>"

resource "aws_s3_bucket" "results" {
  bucket        = "${local.name}-results-${local.account_id}"
  force_destroy = true # POC: destroy removes the results too

  # Cloud Custodian auto-tags this after creation and an SCP blocks removing it — ignore tags to avoid fighting it.
  lifecycle {
    ignore_changes = [tags, tags_all]
  }
}

resource "aws_s3_bucket_public_access_block" "results" {
  bucket                  = aws_s3_bucket.results.id
  block_public_acls       = true
  block_public_policy     = true
  ignore_public_acls      = true
  restrict_public_buckets = true
}

resource "aws_s3_bucket_server_side_encryption_configuration" "results" {
  bucket = aws_s3_bucket.results.id

  rule {
    apply_server_side_encryption_by_default {
      sse_algorithm = "AES256"
    }
  }
}

resource "aws_s3_bucket_lifecycle_configuration" "results" {
  bucket = aws_s3_bucket.results.id

  rule {
    id     = "expire-runs"
    status = "Enabled"

    filter {
      prefix = "runs/"
    }

    expiration {
      days = var.results_retention_days
    }
  }
}

resource "aws_s3_bucket_policy" "results_tls_only" {
  bucket = aws_s3_bucket.results.id

  policy = jsonencode({
    Version = "2012-10-17"
    Statement = [{
      Sid       = "DenyNonTLS"
      Effect    = "Deny"
      Principal = "*"
      Action    = "s3:*"
      Resource  = [aws_s3_bucket.results.arn, "${aws_s3_bucket.results.arn}/*"]
      Condition = { Bool = { "aws:SecureTransport" = "false" } }
    }]
  })

  depends_on = [aws_s3_bucket_public_access_block.results]
}

# --- load-generator instance -------------------------------------------------------------------------------------

resource "aws_security_group" "jmeter" {
  name        = "${local.name}-jmeter"
  description = "JMeter load generator: outbound HTTPS only, nothing inbound"
  vpc_id      = module.network.vpc_id

  egress {
    description = "HTTPS to the target, S3 and the dnf repos (DNS to the VPC resolver is not SG-filtered)"
    from_port   = 443
    to_port     = 443
    protocol    = "tcp"
    cidr_blocks = ["0.0.0.0/0"]
  }

  # Cloud Custodian auto-tags this after creation and an SCP blocks removing it — ignore tags to avoid fighting it.
  lifecycle {
    ignore_changes = [tags, tags_all]
  }
}

resource "aws_iam_role" "jmeter" {
  name = "${local.name}-jmeter"

  assume_role_policy = jsonencode({
    Version = "2012-10-17"
    Statement = [{
      Effect    = "Allow"
      Principal = { Service = "ec2.amazonaws.com" }
      Action    = "sts:AssumeRole"
    }]
  })

  # Cloud Custodian auto-tags this after creation and an SCP blocks removing it — ignore tags to avoid fighting it.
  lifecycle {
    ignore_changes = [tags, tags_all]
  }
}

# Read the tools and test plans, write results — nothing else.
resource "aws_iam_role_policy" "jmeter_s3" {
  name = "results-bucket"
  role = aws_iam_role.jmeter.id

  policy = jsonencode({
    Version = "2012-10-17"
    Statement = [
      {
        Effect = "Allow"
        Action = "s3:GetObject"
        Resource = [
          "${aws_s3_bucket.results.arn}/tools/*",
          "${aws_s3_bucket.results.arn}/runs/*/plan.jmx",
          "${aws_s3_bucket.results.arn}/runs/*/config.properties",
        ]
      },
      {
        Effect   = "Allow"
        Action   = "s3:PutObject"
        Resource = ["${aws_s3_bucket.results.arn}/runs/*/results/*", "${aws_s3_bucket.results.arn}/runs/*/DONE"]
      },
    ]
  })
}

resource "aws_iam_instance_profile" "jmeter" {
  name = "${local.name}-jmeter"
  role = aws_iam_role.jmeter.name

  # Cloud Custodian auto-tags this after creation and an SCP blocks removing it — ignore tags to avoid fighting it.
  lifecycle {
    ignore_changes = [tags, tags_all]
  }
}

data "aws_ssm_parameter" "al2023" {
  name = "/aws/service/ami-amazon-linux-latest/al2023-ami-kernel-default-x86_64"
}

# The workflow launches from this and supplies the instance type and user data (perf-tests/run-on-instance.sh).
resource "aws_launch_template" "jmeter" {
  name                                 = "${local.name}-jmeter"
  image_id                             = nonsensitive(data.aws_ssm_parameter.al2023.value)
  instance_type                        = var.default_instance_type
  instance_initiated_shutdown_behavior = "terminate" # the run script's final `shutdown` deletes the instance
  update_default_version               = true        # each change becomes the default, not just $Latest

  iam_instance_profile {
    arn = aws_iam_instance_profile.jmeter.arn
  }

  network_interfaces {
    subnet_id                   = module.network.private_subnet_ids[0]
    security_groups             = [aws_security_group.jmeter.id]
    associate_public_ip_address = false
    delete_on_termination       = true
  }

  metadata_options {
    http_tokens                 = "required" # IMDSv2 only
    http_put_response_hop_limit = 1
  }

  block_device_mappings {
    device_name = "/dev/xvda"

    ebs {
      volume_size           = var.root_volume_gb
      volume_type           = "gp3"
      encrypted             = true
      delete_on_termination = true
    }
  }

  # Cloud Custodian auto-tags this after creation and an SCP blocks removing it — ignore tags to avoid fighting it.
  lifecycle {
    ignore_changes = [tags, tags_all]
  }
}
