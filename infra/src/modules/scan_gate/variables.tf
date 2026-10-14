variable "environment" {
  type        = string
  description = "Deployment environment name (e.g. dev, prod)."
}

variable "aws_region" {
  type        = string
  description = "Region the watched state machines run in."
}

variable "account_id" {
  type        = string
  description = "Account the watched state machines run in."
}

variable "iam_permissions_boundary_arn" {
  type        = string
  description = "Permissions boundary every IAM role in this estate must carry."
}

variable "watched_workflows" {
  type        = list(string)
  description = "Workflows whose execution status changes re-evaluate the gate. Their state machine ARNs are built from the naming rule, as chained_workflows' are, so watching one never becomes a Terraform dependency on it."
}

variable "reconcile_state_machine_arn" {
  type        = string
  default     = ""
  description = "The reconcile state machine the gate starts once every manifest of a scan is ingested, ahead of its graph load. Empty where the environment does not run reconcile: the gate then starts the graph load straight away."
}

variable "graph_load_state_machine_arn" {
  type        = string
  description = "The graph-load state machine the gate starts once every manifest of a scan is ingested and its reconcile, where deployed, is terminal."
}

variable "pai_state_machine_arn" {
  type        = string
  default     = ""
  description = "The PAI state machine the gate starts once a scan's graph load succeeded and ATRE and Ownership are done. Empty where the environment does not run PAI: the gate then only starts graph loads."
}

variable "uploads_bucket_name" {
  type        = string
  description = "The bucket Thor.TaskApi presigns uploads into — HEADed for Pending files."
}

variable "uploads_bucket_arn" {
  type        = string
  description = "ARN of uploads_bucket_name."
}

variable "handler_source_dir" {
  type        = string
  description = "Absolute path to Thor.ScanGate's dotnet publish output. Terraform zips it; it does not compile (scripts/publish-lambda-functions.sh does)."
}

variable "handler_environment" {
  type        = map(string)
  description = "Environment the handler needs beyond what this module sets itself (the Master DB connection)."
}

variable "rds_db_connect_resources" {
  type        = list(string)
  description = "rds-db:connect resources for the Master DB and tenant users, the same the workflows are granted."
}

variable "private_subnet_ids" {
  type        = list(string)
  description = "Subnets the handler runs in — it reaches RDS through the proxy, S3 through the gateway endpoint and Step Functions through the states interface endpoint."
}

variable "security_group_id" {
  type        = string
  description = "The graph-load workflow's security group. The gate is graph-load's trigger, as CreateManifest is ingestion's, and the RDS proxy already admits that group."
}

variable "upload_grace_seconds" {
  type        = number
  default     = 900
  description = "How long after a presigned URL expires an upload may still be finishing. S3 checks the signature when a PUT starts."
}

variable "queue_visibility_timeout_seconds" {
  type        = number
  default     = 300
  description = "How long a message the gate asks to recheck stays hidden — the gate's only timer. Must exceed handler_timeout."
}

variable "queue_max_receive_count" {
  type        = number
  default     = 20
  description = "Deliveries before a message moves to the DLQ. With the visibility timeout this bounds how long a recheck is retried; 20 x 300 s comfortably covers a 15-minute URL plus its grace."
}

variable "handler_timeout" {
  type    = number
  default = 60
}

variable "handler_memory_size" {
  type    = number
  default = 512
}

variable "handler_runtime" {
  type    = string
  default = "dotnet10"
}

variable "log_retention_days" {
  type    = number
  default = 30
}

variable "tags" {
  type    = map(string)
  default = {}
}
