# AWS Network Firewall POC — closes the raw-IP gap the DNS Firewall leaves. Everything here is gated on
# var.enable_network_firewall, so `-var enable_network_firewall=false` removes it again.
#
# Path out:  private subnet → firewall endpoint → NAT → IGW.   Path back: NAT → firewall endpoint → private subnet.
# The firewall lets TCP handshakes through, then passes only TLS whose SNI is on the allowlist below; every other
# established flow (no SNI, unlisted SNI, plain TCP/UDP) is dropped and logged.
#
# POC shortcuts, not a design for the real module:
#  - One endpoint, in the NAT's AZ; the other AZ's private subnet crosses AZs to reach it.
#  - The module already routes private 0.0.0.0/0 → NAT, so two /1 routes (more specific, so they win) send it to
#    the firewall instead, without touching the module.
#  - The module doesn't output the NAT or public route table, so they're looked up — the base stack must exist first.

locals {
  nfw = var.enable_network_firewall ? 1 : 0

  # Mirrors the DNS Firewall allowlist (rule 200 + egress_allowed_domains) and the tenant-vendor added in step 2.
  nfw_rules = <<-EOT
    pass tls $HOME_NET any -> $EXTERNAL_NET 443 (tls.sni; dotprefix; content:".amazonaws.com"; endswith; nocase; flow:to_server; msg:"allow *.amazonaws.com"; sid:100; rev:1;)
    pass tls $HOME_NET any -> $EXTERNAL_NET 443 (tls.sni; content:"example.org"; startswith; endswith; nocase; flow:to_server; msg:"allow example.org"; sid:101; rev:1;)
    pass tls $HOME_NET any -> $EXTERNAL_NET 443 (tls.sni; content:"www.wikipedia.org"; startswith; endswith; nocase; flow:to_server; msg:"allow tenant vendor www.wikipedia.org"; sid:102; rev:1;)
  EOT
}

data "aws_nat_gateway" "poc" {
  count  = local.nfw
  vpc_id = module.network.vpc_id
  state  = "available"
}

data "aws_subnet" "nat" {
  count = local.nfw
  id    = data.aws_nat_gateway.poc[0].subnet_id
}

data "aws_route_table" "public" {
  count  = local.nfw
  vpc_id = module.network.vpc_id

  filter {
    name   = "tag:Name"
    values = ["thor-poc-public-rt"]
  }
}

resource "aws_subnet" "firewall" {
  count             = local.nfw
  vpc_id            = module.network.vpc_id
  cidr_block        = "10.99.20.0/28"
  availability_zone = data.aws_subnet.nat[0].availability_zone

  tags = { Name = "thor-poc-firewall-${data.aws_subnet.nat[0].availability_zone}" }

  # Cloud Custodian auto-tags this after creation and an SCP blocks removing it — ignore tags to avoid fighting it.
  lifecycle {
    ignore_changes = [tags, tags_all]
  }
}

resource "aws_networkfirewall_rule_group" "egress_allowlist" {
  count    = local.nfw
  name     = "thor-poc-egress-allowlist"
  type     = "STATEFUL"
  capacity = 100

  rule_group {
    rules_source {
      rules_string = local.nfw_rules
    }
    stateful_rule_options {
      rule_order = "STRICT_ORDER"
    }
  }

  # Cloud Custodian auto-tags this after creation and an SCP blocks removing it — ignore tags to avoid fighting it.
  lifecycle {
    ignore_changes = [tags, tags_all]
  }
}

resource "aws_networkfirewall_firewall_policy" "egress" {
  count = local.nfw
  name  = "thor-poc-egress"

  firewall_policy {
    stateless_default_actions          = ["aws:forward_to_sfe"]
    stateless_fragment_default_actions = ["aws:forward_to_sfe"]

    stateful_engine_options {
      rule_order = "STRICT_ORDER"
    }
    # Anything the allowlist didn't pass is dropped once the flow is established, and logged.
    stateful_default_actions = ["aws:drop_established", "aws:alert_established"]

    stateful_rule_group_reference {
      priority     = 1
      resource_arn = aws_networkfirewall_rule_group.egress_allowlist[0].arn
    }
  }

  # Cloud Custodian auto-tags this after creation and an SCP blocks removing it — ignore tags to avoid fighting it.
  lifecycle {
    ignore_changes = [tags, tags_all]
  }
}

resource "aws_networkfirewall_firewall" "egress" {
  count               = local.nfw
  name                = "thor-poc-egress"
  vpc_id              = module.network.vpc_id
  firewall_policy_arn = aws_networkfirewall_firewall_policy.egress[0].arn
  delete_protection   = false

  subnet_mapping {
    subnet_id = aws_subnet.firewall[0].id
  }

  # Cloud Custodian auto-tags this after creation and an SCP blocks removing it — ignore tags to avoid fighting it.
  lifecycle {
    ignore_changes = [tags, tags_all]
  }
}

locals {
  nfw_endpoint_id = var.enable_network_firewall ? one([
    for s in aws_networkfirewall_firewall.egress[0].firewall_status[0].sync_states : s.attachment[0].endpoint_id
  ]) : null
}

# Firewall subnet → NAT.
resource "aws_route_table" "firewall" {
  count  = local.nfw
  vpc_id = module.network.vpc_id

  tags = { Name = "thor-poc-firewall-rt" }

  # Cloud Custodian auto-tags this after creation and an SCP blocks removing it — ignore tags to avoid fighting it.
  lifecycle {
    ignore_changes = [tags, tags_all]
  }
}

resource "aws_route" "firewall_to_nat" {
  count                  = local.nfw
  route_table_id         = aws_route_table.firewall[0].id
  destination_cidr_block = "0.0.0.0/0"
  nat_gateway_id         = data.aws_nat_gateway.poc[0].id
}

resource "aws_route_table_association" "firewall" {
  count          = local.nfw
  subnet_id      = aws_subnet.firewall[0].id
  route_table_id = aws_route_table.firewall[0].id
}

# Private subnets → firewall. Two /1s beat the module's 0.0.0.0/0 → NAT.
resource "aws_route" "private_to_firewall" {
  for_each               = var.enable_network_firewall ? toset(["0.0.0.0/1", "128.0.0.0/1"]) : toset([])
  route_table_id         = module.network.private_route_table_id
  destination_cidr_block = each.value
  vpc_endpoint_id        = local.nfw_endpoint_id
}

# NAT subnet → firewall for replies to the private subnets, so both directions pass the firewall.
resource "aws_route" "public_return_to_firewall" {
  for_each               = var.enable_network_firewall ? toset(local.private_subnet_cidrs) : toset([])
  route_table_id         = data.aws_route_table.public[0].id
  destination_cidr_block = each.value
  vpc_endpoint_id        = local.nfw_endpoint_id
}

resource "aws_cloudwatch_log_group" "nfw_alerts" {
  count             = local.nfw
  name              = "/aws/network-firewall/thor-poc"
  retention_in_days = 7

  # Cloud Custodian auto-tags this after creation and an SCP blocks removing it — ignore tags to avoid fighting it.
  lifecycle {
    ignore_changes = [tags, tags_all]
  }
}

resource "aws_networkfirewall_logging_configuration" "egress" {
  count        = local.nfw
  firewall_arn = aws_networkfirewall_firewall.egress[0].arn

  logging_configuration {
    log_destination_config {
      log_destination      = { logGroup = aws_cloudwatch_log_group.nfw_alerts[0].name }
      log_destination_type = "CloudWatchLogs"
      log_type             = "ALERT"
    }
  }
}
