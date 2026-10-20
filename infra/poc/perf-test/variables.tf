variable "environment" {
  type        = string
  description = "Environment name — used in resource names (thor-<env>-perf-*) and as the GitHub environment the workflow runs in"
  default     = "prod"
}

variable "aws_region" {
  type    = string
  default = "us-east-1"
}

variable "default_instance_type" {
  type = string
  # QA wants c5n.2xlarge (25 Gbps; c5n.4xlarge later), but the sandbox account's SCP blocks every c5n size.
  # t3.xlarge (4 vCPU, 16 GiB — fits the 8 GB JMeter heap) until an SCP exception is granted.
  description = "Launch template instance type"
  default     = "t3.xlarge"
}

variable "root_volume_gb" {
  type        = number
  description = "QA requirement: 50 GB gp3 for the ZIP data pool, logs and results"
  default     = 50
}

variable "results_retention_days" {
  type    = number
  default = 30
}

variable "egress_allowed_domains" {
  type        = list(string)
  description = "DNS Firewall allowlist extras: the test target and the AL2023 dnf mirror"
  default     = ["jsonplaceholder.typicode.com", "cdn.amazonlinux.com"]
}

variable "github_sub_prefix" {
  type        = string
  description = "This repo's OIDC subject prefix (gh api repos/<owner>/<repo>/actions/oidc/customization/sub)"
  default     = "repo:Ajjai99@73207164/thor-backend-nat@1418494428"
}
