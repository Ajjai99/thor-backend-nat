variable "aws_region" {
  type    = string
  default = "us-east-1"
}

variable "default_action" {
  type        = string
  description = "Rule 300 action for names on no list: BLOCK or ALERT"
  default     = "BLOCK"
}

variable "enable_network_firewall" {
  type        = bool
  description = "Route private-subnet egress through AWS Network Firewall (SNI allowlist) before the NAT. ~$0.395/h while on"
  default     = false
}

variable "egress_allowed_domains" {
  type        = list(string)
  description = "Rule 200 extras. example.org is an exact-name entry, so www.example.org should still hit rule 300"
  default     = ["example.org"]
}
