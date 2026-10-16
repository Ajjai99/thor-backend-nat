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
  type        = string
  description = "Launch template default; the workflow can override it per run"
  default     = "t3.medium"
}

variable "root_volume_gb" {
  type        = number
  description = "Holds JMeter, the .jtl results and the HTML report — millions of samples need several GB"
  default     = 30
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
