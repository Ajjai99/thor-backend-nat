terraform {
  required_version = ">= 1.15"

  required_providers {
    aws = {
      source  = "hashicorp/aws"
      version = "~> 6.0"
    }
    archive = {
      source  = "hashicorp/archive"
      version = "~> 2.0"
    }
  }
}

# The scan-completion gate (backend/functions/Thor.ScanGate), in two stages:
#   1. every task of a scan finished and every manifest ingested -> starts the scan's reconcile
#      (reconcile-{scanId}) where the environment runs it, and once that is terminal - succeeded or
#      failed - the scan's graph load (graph-load-{scanId});
#   2. that load succeeded, and ATRE and Ownership terminal for every manifest -> starts PAI
#      (pai-{scanId}), where the environment runs PAI.
#
#   Step Functions status change (ingestion/ATRE/Ownership/reconcile/graph-load) --EventBridge--> queue --> handler --> StartExecution
#   Thor.TaskApi task reaching a terminal status ----------------------------------------> queue
#
# The queue is also the gate's only timer. A scan waiting on an upload URL that has not expired
# emits no event if the upload never happens, so the handler returns that message as a batch-item
# failure and it comes back after the visibility timeout. The handler runs in a VPC with no SQS
# endpoint and never calls SQS itself — the event source mapping does the polling and deleting.

locals {
  name_prefix = "thor-${var.environment}-scan-gate"

  watched_state_machine_arns = [
    for name in var.watched_workflows :
    "arn:aws:states:${var.aws_region}:${var.account_id}:stateMachine:thor-${var.environment}-workflow-${name}-sf"
  ]
}

# --- queue --------------------------------------------------------------------------------------

resource "aws_sqs_queue" "dlq" {
  name                      = "${local.name_prefix}-dlq"
  message_retention_seconds = 1209600

  tags = var.tags

  # Cloud Custodian auto-tags this after creation and an SCP blocks removing it — ignore tags to avoid fighting it.
  lifecycle {
    ignore_changes = [tags, tags_all]
  }
}

resource "aws_sqs_queue" "gate" {
  name                       = "${local.name_prefix}-sqs"
  visibility_timeout_seconds = var.queue_visibility_timeout_seconds

  redrive_policy = jsonencode({
    deadLetterTargetArn = aws_sqs_queue.dlq.arn
    maxReceiveCount     = var.queue_max_receive_count
  })

  tags = var.tags

  # Cloud Custodian auto-tags this after creation and an SCP blocks removing it — ignore tags to avoid fighting it.
  lifecycle {
    ignore_changes = [tags, tags_all]
  }
}

# Without it EventBridge's deliveries are silently dropped.
data "aws_iam_policy_document" "gate_queue" {
  statement {
    actions   = ["sqs:SendMessage"]
    resources = [aws_sqs_queue.gate.arn]

    principals {
      type        = "Service"
      identifiers = ["events.amazonaws.com"]
    }

    condition {
      test     = "ArnEquals"
      variable = "aws:SourceArn"
      values   = [aws_cloudwatch_event_rule.execution_status_change.arn]
    }
  }
}

resource "aws_sqs_queue_policy" "gate" {
  queue_url = aws_sqs_queue.gate.id
  policy    = data.aws_iam_policy_document.gate_queue.json
}

# --- execution status changes --------------------------------------------------------------------
#
# Step Functions sends these to the default bus for every standard execution with no opt-in. The
# event carries the execution's own input, which is where the handler reads TenantId and the
# manifest or scan. Matching exact ARNs also excludes Distributed Map child executions, whose ARNs
# carry a map-run suffix.

resource "aws_cloudwatch_event_rule" "execution_status_change" {
  name        = "${local.name_prefix}-execution-status-change"
  description = "A workflow whose completion can complete a scan reached a terminal status."

  event_pattern = jsonencode({
    source      = ["aws.states"]
    detail-type = ["Step Functions Execution Status Change"]
    detail = {
      status          = ["SUCCEEDED", "FAILED", "TIMED_OUT", "ABORTED"]
      stateMachineArn = local.watched_state_machine_arns
    }
  })

  tags = var.tags

  # Cloud Custodian auto-tags this after creation and an SCP blocks removing it — ignore tags to avoid fighting it.
  lifecycle {
    ignore_changes = [tags, tags_all]
  }
}

resource "aws_cloudwatch_event_target" "gate_queue" {
  rule = aws_cloudwatch_event_rule.execution_status_change.name
  arn  = aws_sqs_queue.gate.arn
}

# --- handler ------------------------------------------------------------------------------------

data "aws_iam_policy_document" "handler_assume" {
  statement {
    actions = ["sts:AssumeRole"]

    principals {
      type        = "Service"
      identifiers = ["lambda.amazonaws.com"]
    }
  }
}

# output_path uses dirname(), not "${var.handler_source_dir}/..": CI's apply job restores the zip
# alone, never publish/, and a path routed through a directory that doesn't exist fails to open even
# when the file does.
data "archive_file" "handler" {
  type        = "zip"
  source_dir  = var.handler_source_dir
  output_path = "${dirname(var.handler_source_dir)}/scan-gate-build.zip"
}

resource "aws_iam_role" "handler" {
  name                 = local.name_prefix
  assume_role_policy   = data.aws_iam_policy_document.handler_assume.json
  permissions_boundary = var.iam_permissions_boundary_arn
  tags                 = var.tags

  # Cloud Custodian auto-tags this after creation and an SCP blocks removing it — ignore tags to avoid fighting it.
  lifecycle {
    ignore_changes = [tags, tags_all]
  }
}

# VPC variant of AWSLambdaBasicExecutionRole — adds the ENI management an in-VPC function needs.
resource "aws_iam_role_policy_attachment" "handler_vpc_access" {
  role       = aws_iam_role.handler.name
  policy_arn = "arn:aws:iam::aws:policy/service-role/AWSLambdaVPCAccessExecutionRole"
}

data "aws_iam_policy_document" "handler" {
  statement {
    actions   = ["sqs:ReceiveMessage", "sqs:DeleteMessage", "sqs:GetQueueAttributes"]
    resources = [aws_sqs_queue.gate.arn]
  }

  statement {
    actions   = ["rds-db:connect"]
    resources = var.rds_db_connect_resources
  }

  # HeadObject is authorised by s3:GetObject.
  statement {
    actions   = ["s3:GetObject"]
    resources = ["${var.uploads_bucket_arn}/tenants/*"]
  }

  # Without ListBucket, S3 answers HeadObject on a missing key with 403 rather than 404, and the gate
  # could never tell an upload that never happened from a permissions fault. Not prefix-conditioned:
  # s3:prefix is absent on a HeadObject request, so such a condition would never match.
  statement {
    actions   = ["s3:ListBucket"]
    resources = [var.uploads_bucket_arn]
  }

  statement {
    actions   = ["states:StartExecution"]
    resources = [var.graph_load_state_machine_arn]
  }

  dynamic "statement" {
    for_each = var.reconcile_state_machine_arn == "" ? [] : [1]

    content {
      actions   = ["states:StartExecution"]
      resources = [var.reconcile_state_machine_arn]
    }
  }

  dynamic "statement" {
    for_each = var.pai_state_machine_arn == "" ? [] : [1]

    content {
      actions   = ["states:StartExecution"]
      resources = [var.pai_state_machine_arn]
    }
  }
}

resource "aws_iam_role_policy" "handler" {
  name   = "${local.name_prefix}-permissions"
  role   = aws_iam_role.handler.id
  policy = data.aws_iam_policy_document.handler.json
}

resource "aws_cloudwatch_log_group" "handler" {
  name              = "/aws/lambda/${local.name_prefix}"
  retention_in_days = var.log_retention_days
  tags              = var.tags

  # Cloud Custodian auto-tags this after creation and an SCP blocks removing it — ignore tags to avoid fighting it.
  lifecycle {
    ignore_changes = [tags, tags_all]
  }
}

resource "aws_lambda_function" "handler" {
  function_name    = local.name_prefix
  role             = aws_iam_role.handler.arn
  runtime          = var.handler_runtime
  handler          = "Thor.ScanGate.Function::Thor.ScanGate.Function.Function::FunctionHandler"
  filename         = data.archive_file.handler.output_path
  source_code_hash = data.archive_file.handler.output_base64sha256
  timeout          = var.handler_timeout
  memory_size      = var.handler_memory_size

  # No NAT on these subnets: S3 is reached through modules/network's gateway endpoint and Step
  # Functions through its states interface endpoint.
  vpc_config {
    subnet_ids         = var.private_subnet_ids
    security_group_ids = [var.security_group_id]
  }

  environment {
    variables = merge(var.handler_environment, {
      THOR_UPLOADS_BUCKET                 = var.uploads_bucket_name
      THOR_RECONCILE_STATE_MACHINE_ARN    = var.reconcile_state_machine_arn
      THOR_GRAPH_LOAD_STATE_MACHINE_ARN   = var.graph_load_state_machine_arn
      THOR_PAI_STATE_MACHINE_ARN          = var.pai_state_machine_arn
      THOR_SCAN_GATE_UPLOAD_GRACE_SECONDS = tostring(var.upload_grace_seconds)
    })
  }

  tags = var.tags

  depends_on = [
    aws_cloudwatch_log_group.handler,
    aws_iam_role_policy_attachment.handler_vpc_access,
    aws_iam_role_policy.handler,
  ]

  # Cloud Custodian auto-tags this after creation and an SCP blocks removing it — ignore tags to avoid fighting it.
  lifecycle {
    ignore_changes = [tags, tags_all]
  }
}

# ReportBatchItemFailures is what lets the handler hand back only the messages to recheck.
resource "aws_lambda_event_source_mapping" "gate_queue" {
  event_source_arn        = aws_sqs_queue.gate.arn
  function_name           = aws_lambda_function.handler.arn
  batch_size              = 10
  function_response_types = ["ReportBatchItemFailures"]

  depends_on = [aws_iam_role_policy.handler]
}
