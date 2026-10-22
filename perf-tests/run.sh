#!/usr/bin/env bash
# THOR Performance Test — EC2 CLI runner
# Usage: ./run.sh [overrides...]
# Example: ./run.sh -Jthread.count=50 -Jtest.duration.seconds=3600

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
RESULTS_DIR="${SCRIPT_DIR}/results"
TIMESTAMP="$(date +%Y%m%d_%H%M%S)"
RESULT_FILE="${RESULTS_DIR}/result_${TIMESTAMP}.jtl"
LOG_FILE="${RESULTS_DIR}/jmeter_${TIMESTAMP}.log"
REPORT_DIR="${RESULTS_DIR}/report_${TIMESTAMP}"

mkdir -p "${RESULTS_DIR}"

# ── Verify prerequisites ─────────────────────────────────────────────────────
if ! command -v jmeter &>/dev/null; then
    echo "ERROR: jmeter not found in PATH. Install Apache JMeter and add it to PATH."
    exit 1
fi

if [[ ! -f "${SCRIPT_DIR}/config/test-data.csv" ]]; then
    echo "ERROR: config/test-data.csv not found. Populate it with real client, task and source IDs first."
    exit 1
fi

if [[ ! -d "${SCRIPT_DIR}/zip-pool" ]]; then
    echo "ERROR: zip-pool/ directory not found."
    echo "Run: python ../generate_zip_pool.py"
    exit 1
fi

# ── Run JMeter ───────────────────────────────────────────────────────────────
echo "Starting THOR performance test at ${TIMESTAMP}"
echo "Results → ${RESULT_FILE}"
echo "Log     → ${LOG_FILE}"
echo ""

jmeter \
    -n \
    -t "${SCRIPT_DIR}/test-plan 1.jmx" \
    -p "${SCRIPT_DIR}/config/config.ec2.properties" \
    -l "${RESULT_FILE}" \
    -j "${LOG_FILE}" \
    -Jresult.file="${RESULT_FILE}" \
    "$@"

echo ""
echo "Test complete. Generating HTML report..."

jmeter \
    -g "${RESULT_FILE}" \
    -o "${REPORT_DIR}"

echo "HTML report → ${REPORT_DIR}/index.html"
echo "Done."
