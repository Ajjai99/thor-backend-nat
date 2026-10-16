#!/usr/bin/env bash
# Usage:
#   ./run.sh probe [name ...]       invoke the in-VPC probe (default checks if no names given)
#   ./run.sh add-vendor <domain>    add a domain to the tenant-vendors list (rule 210), as thor-api does at runtime
#   ./run.sh logs [minutes]         firewall matches from the Resolver query logs (default: last 30 min)
#   ./run.sh nfw-logs [minutes]     Network Firewall drops (only with enable_network_firewall=true)
set -euo pipefail
cd "$(dirname "$0")"

out() { terraform output -raw "$1"; }

case "${1:-}" in
  probe)
    shift
    payload=$(python3 -c 'import json,sys; print(json.dumps({"names": sys.argv[1:]}))' "$@")
    aws lambda invoke --function-name "$(out probe_function_name)" \
      --cli-binary-format raw-in-base64-out --payload "$payload" .build/result.json >/dev/null
    python3 - .build/result.json <<'PY'
import json, sys
r = json.load(open(sys.argv[1]))
if "checks" not in r:
    sys.exit(json.dumps(r, indent=2))
print(f"{'NAME':30} {'DNS':28} {'HTTPS':22} EXPECT")
for c in r["checks"]:
    print(f"{c['name']:30} {c['dns']:28} {c['https']:22} {c['expect']}")
print("\nBypass checks (DNS Firewall is not expected to stop these):")
for k, v in r["bypass"].items():
    print(f"  {k:36} {v}")
PY
    ;;
  add-vendor)
    aws route53resolver update-firewall-domains --operation ADD \
      --firewall-domain-list-id "$(out tenant_vendors_domain_list_id)" --domains "$2"
    echo "Added $2 — allow ~1 min for the firewall to pick it up before probing."
    ;;
  logs)
    minutes="${2:-30}"
    lists=$(aws route53resolver list-firewall-domain-lists --query 'FirewallDomainLists[].[Id,Name]' --output json)
    qid=$(aws logs start-query --log-group-name "$(out query_log_group)" \
      --start-time $(( $(date +%s) - minutes * 60 )) --end-time "$(date +%s)" \
      --query-string 'fields @timestamp, query_name, rcode, firewall_rule_action, firewall_domain_list_id, srcaddr
        | filter ispresent(firewall_rule_action) | sort @timestamp desc | limit 100' \
      --query queryId --output text)
    until [ "$(aws logs get-query-results --query-id "$qid" --query status --output text)" = "Complete" ]; do sleep 1; done
    aws logs get-query-results --query-id "$qid" --output json > .build/logs.json
    LISTS="$lists" python3 - .build/logs.json <<'PY'
import json, os, sys
names = dict(json.loads(os.environ["LISTS"]))
rows = json.load(open(sys.argv[1]))["results"]
print(f"{'TIME':24} {'ACTION':7} {'RCODE':9} {'QUERY':32} DOMAIN LIST")
for row in rows:
    f = {x["field"]: x["value"] for x in row}
    print(f"{f['@timestamp']:24} {f.get('firewall_rule_action', ''):7} {f.get('rcode', ''):9} "
          f"{f.get('query_name', ''):32} {names.get(f.get('firewall_domain_list_id'), '?')}")
print(f"\n{len(rows)} firewall matches. Query logs can lag a few minutes behind the probe.")
PY
    ;;
  nfw-logs)
    minutes="${2:-30}"
    qid=$(aws logs start-query --log-group-name /aws/network-firewall/thor-poc \
      --start-time $(( $(date +%s) - minutes * 60 )) --end-time "$(date +%s)" \
      --query-string 'fields @timestamp, event.alert.action, event.dest_ip, event.dest_port, event.proto, event.tls.sni
        | sort @timestamp desc | limit 100' \
      --query queryId --output text)
    until [ "$(aws logs get-query-results --query-id "$qid" --query status --output text)" = "Complete" ]; do sleep 1; done
    aws logs get-query-results --query-id "$qid" --output json > .build/nfw-logs.json
    python3 - .build/nfw-logs.json <<'PY'
import json, sys
rows = json.load(open(sys.argv[1]))["results"]
print(f"{'TIME':24} {'ACTION':8} {'PROTO':6} {'DEST':22} SNI")
for row in rows:
    f = {x["field"]: x["value"] for x in row}
    dest = f"{f.get('event.dest_ip', '')}:{f.get('event.dest_port', '')}"
    print(f"{f['@timestamp']:24} {f.get('event.alert.action', ''):8} {f.get('event.proto', ''):6} {dest:22} "
          f"{f.get('event.tls.sni', '(none)')}")
print(f"\n{len(rows)} alerts. Firewall logs can lag a few minutes behind the probe.")
PY
    ;;
  *)
    sed -n '2,6p' "$0"; exit 1 ;;
esac
