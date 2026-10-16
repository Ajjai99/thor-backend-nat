# DNS Firewall POC

Proves what the Route 53 Resolver DNS Firewall in `infra/src/modules/network/dns_firewall.tf` blocks and lets through.

It builds a **separate throwaway VPC** (`thor-poc-*`, `10.99.0.0/16`) from the real `network` module with
`nat_gateway_mode = "single"`. It never touches dev/qa/prod. A Python Lambda in the private subnets resolves
names through the VPC resolver and opens a TLS connection out through the NAT.

Cost while up: one NAT Gateway + EIP (~$0.05/h) plus pennies of Lambda and logs. Destroy when done.

## Run

Needs AWS credentials for a sandbox/dev account (`aws sts get-caller-identity` works).

```bash
cd infra/poc/dns-firewall
terraform init
terraform apply                       # default_action = BLOCK, egress_allowed_domains = ["example.org"]
./run.sh probe                        # step 1
./run.sh add-vendor www.wikipedia.org # step 2: runtime tenant-vendor add (rule 210)
./run.sh probe www.wikipedia.org
terraform apply -var default_action=ALERT
./run.sh probe                        # step 3
./run.sh logs 60                      # step 4: what the query logs recorded
terraform destroy
```

## Expected results

Step 1 (`default_action = BLOCK`):

| Name                          | Rule | DNS              | HTTPS     |
|-------------------------------|------|------------------|-----------|
| `pypi.org`                    | 100  | FAILED (NXDOMAIN)| -         |
| `deb.debian.org`              | 100  | FAILED           | -         |
| `sts.us-east-1.amazonaws.com` | 200  | resolves         | CONNECTED |
| `example.org`                 | 200  | resolves         | CONNECTED |
| `www.example.org`             | 300  | FAILED           | -         |
| `www.wikipedia.org`           | 300  | FAILED           | -         |
| `example.com`                 | 300  | FAILED           | -         |

Step 2: `www.wikipedia.org` resolves and connects (rule 210) with no Terraform apply.

Step 3 (`default_action = ALERT`): every rule-300 name resolves and connects; rule 100 still blocks.

Step 4: `logs` shows each match with its action (`BLOCK` / `ALLOW` / `ALERT`) and the domain list it hit.

## Network Firewall add-on (`network_firewall.tf`)

Closes the raw-IP gap. Adds one firewall endpoint (~$0.395/h) between the private subnets and the NAT; it passes
only TLS whose SNI is on its allowlist (`*.amazonaws.com`, `example.org`, `www.wikipedia.org`) and drops the rest.

```bash
terraform apply -var default_action=ALERT -var enable_network_firewall=true
./run.sh probe
./run.sh nfw-logs 30
terraform apply -var default_action=ALERT -var enable_network_firewall=false   # remove it again
```

DNS stays in ALERT so unlisted names still resolve, which shows the Network Firewall doing the blocking:
`example.com` and `www.example.org` resolve but HTTPS fails; `1.1.1.1` with no SNI fails. `1.1.1.1` with a faked
SNI of `example.org` still gets through — SNI spoofing, which only TLS inspection closes.

**Bypass checks, in every step:** a TLS connect to `1.1.1.1:443` and a DNS query straight to `8.8.8.8`
both **succeed**. The DNS Firewall only filters lookups that go through the VPC resolver, so it can't stop
connections to raw IPs or queries to an outside resolver. Closing that needs SG/NACL egress rules or AWS
Network Firewall.
