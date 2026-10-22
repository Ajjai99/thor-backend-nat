#!/usr/bin/env bash
# THOR Dry Run — local validation before EC2
# Usage: ./run-dryrun.sh [overrides...]
# Example: ./run-dryrun.sh -Jthread.count=10 -Jtest.duration.seconds=300

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
RESULTS_DIR="${SCRIPT_DIR}/results"
TIMESTAMP="$(date +%Y%m%d_%H%M%S)"
RESULT_FILE="${RESULTS_DIR}/dryrun_${TIMESTAMP}.jtl"
LOG_FILE="${RESULTS_DIR}/dryrun_${TIMESTAMP}.log"
REPORT_DIR="${RESULTS_DIR}/dryrun_report_${TIMESTAMP}"

mkdir -p "${RESULTS_DIR}"

# ── Verify prerequisites ─────────────────────────────────────────────────────
if ! command -v jmeter &>/dev/null; then
    echo "ERROR: jmeter not found in PATH. Install Apache JMeter and add it to PATH."
    exit 1
fi

if [[ ! -f "${SCRIPT_DIR}/config/test-data.csv" ]]; then
    echo "ERROR: config/test-data.csv not found. Fill in client and task data first."
    exit 1
fi

if [[ ! -d "${SCRIPT_DIR}/zip-pool" ]]; then
    echo "ERROR: zip-pool/ not found. Run: python ../generate_zip_pool.py"
    exit 1
fi

# ── Run JMeter ───────────────────────────────────────────────────────────────
echo "Starting THOR dry run at ${TIMESTAMP}"
echo "Threads : 5  |  Duration : 120s  |  Ramp-up : 10s"
echo "Results → ${RESULT_FILE}"
echo "Log     → ${LOG_FILE}"
echo ""

jmeter \
    -n \
    -t "${SCRIPT_DIR}/test-plan_smoke.jmx" \
    -p "${SCRIPT_DIR}/config/config.dryrun.properties" \
    -l "${RESULT_FILE}" \
    -j "${LOG_FILE}" \
    "$@"

echo ""
echo "Dry run complete. Generating HTML report..."

jmeter \
    -g "${RESULT_FILE}" \
    -o "${REPORT_DIR}"

echo "HTML report → ${REPORT_DIR}/index.html"
echo ""
echo "Check before EC2:"
echo "  - All Register API calls → HTTP 200"
echo "  - All Upload API calls   → HTTP 200 (presigned URL extracted)"
echo "  - All S3 PUT calls       → HTTP 200 or 204"
echo "  - Error rate             → 0%"
