# Throwaway POC: a separate VPC built from the real network module (single NAT + DNS Firewall), plus one
# in-VPC Lambda that probes egress. Local state, no Terragrunt — run it, read the results, destroy it.

terraform {
  required_version = ">= 1.15"

  required_providers {
    aws = {
      source  = "hashicorp/aws"
      version = "~> 6.0"
    }
    archive = {
      source  = "hashicorp/archive"
      version = "~> 2.4"
    }
  }
}

provider "aws" {
  region = var.aws_region

  default_tags {
    tags = {
      Project     = "thor-platform"
      Environment = "poc"
      ManagedBy   = "terraform-poc-dns-firewall"
    }
  }
}

locals {
  private_subnet_cidrs = ["10.99.10.0/24", "10.99.11.0/24"]
}

module "network" {
  source = "../../src/modules/network"

  environment          = "poc"
  vpc_cidr             = "10.99.0.0/16"
  az_count             = 2
  private_subnet_cidrs = local.private_subnet_cidrs
  public_subnet_cidrs  = ["10.99.0.0/24", "10.99.1.0/24"]
  nat_gateway_mode     = "single"

  # Not needed to exercise the firewall, and each interface endpoint costs money while the POC is up.
  enable_vpc_endpoints = false

  dns_firewall_default_action = var.default_action
  egress_allowed_domains      = var.egress_allowed_domains
}

resource "aws_security_group" "probe" {
  name        = "thor-poc-dns-probe"
  description = "Egress-only SG for the DNS Firewall probe Lambda"
  vpc_id      = module.network.vpc_id

  # Same open egress the real services have — the POC shows what the DNS Firewall alone does and does not stop.
  egress {
    from_port   = 0
    to_port     = 0
    protocol    = "-1"
    cidr_blocks = ["0.0.0.0/0"]
  }

  # Cloud Custodian auto-tags this after creation and an SCP blocks removing it — ignore tags to avoid fighting it.
  lifecycle {
    ignore_changes = [tags, tags_all]
  }
}

resource "aws_iam_role" "probe" {
  name = "thor-poc-dns-probe"

  assume_role_policy = jsonencode({
    Version = "2012-10-17"
    Statement = [{
      Effect    = "Allow"
      Principal = { Service = "lambda.amazonaws.com" }
      Action    = "sts:AssumeRole"
    }]
  })

  # Cloud Custodian auto-tags this after creation and an SCP blocks removing it — ignore tags to avoid fighting it.
  lifecycle {
    ignore_changes = [tags, tags_all]
  }
}

resource "aws_iam_role_policy_attachment" "probe_vpc" {
  role       = aws_iam_role.probe.name
  policy_arn = "arn:aws:iam::aws:policy/service-role/AWSLambdaVPCAccessExecutionRole"
}

data "archive_file" "probe" {
  type        = "zip"
  source_file = "${path.module}/handler.py"
  output_path = "${path.module}/.build/probe.zip"
}

resource "aws_lambda_function" "probe" {
  function_name    = "thor-poc-dns-probe"
  role             = aws_iam_role.probe.arn
  runtime          = "python3.12"
  handler          = "handler.handler"
  filename         = data.archive_file.probe.output_path
  source_code_hash = data.archive_file.probe.output_base64sha256
  timeout          = 60

  vpc_config {
    subnet_ids         = module.network.private_subnet_ids
    security_group_ids = [aws_security_group.probe.id]
  }

  depends_on = [aws_iam_role_policy_attachment.probe_vpc]

  # Cloud Custodian auto-tags this after creation and an SCP blocks removing it — ignore tags to avoid fighting it.
  lifecycle {
    ignore_changes = [tags, tags_all]
  }
}
