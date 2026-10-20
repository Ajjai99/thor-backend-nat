#!/bin/bash
# EC2 user data for one performance-test run, launched by .github/workflows/performance-test.yml. The workflow
# prepends the run's settings as shell variables:
#   BUCKET RUN_ID JMETER_VERSION JMETER_HEAP THREADS LOOPS MAX_MINUTES
# Pulls Java (dnf) and JMeter + the test plan (S3), runs the test, writes results/ and finally DONE to
# s3://$BUCKET/runs/$RUN_ID/, then shuts down — which terminates the instance (launch template setting).
# Everything printed here ends up in results/run.log in S3.
set -euo pipefail

RUN_URI="s3://${BUCKET}/runs/${RUN_ID}"
WORK=/opt/perf
mkdir -p "$WORK/results"
exec > >(tee "$WORK/results/run.log") 2>&1

log() { echo "$(date -u +%H:%M:%S) $*"; }

log "▶ [1/6] Instance started for run ${RUN_ID}"
log "  CPUs: $(nproc), memory: $(free -g | awk '/Mem:/ {print $2}') GiB, free disk: $(df -h / | awk 'NR==2 {print $4}')"
log "  Settings: ${THREADS} users x ${LOOPS} loops, JMeter ${JMETER_VERSION}, heap ${JMETER_HEAP}"

# Hard cap: the instance terminates after MAX_MINUTES even if JMeter hangs.
shutdown -h "+${MAX_MINUTES}"
log "  Safety timer set: the instance terminates in ${MAX_MINUTES} min no matter what"

finish() {
  local code=$?
  log "▶ [6/6] Finishing (exit code ${code})"
  # Logged before the upload so these lines are in the run.log that reaches S3.
  log "  Uploading results to ${RUN_URI}/results/, then writing the DONE marker the GitHub workflow waits for"
  log "✔ After that the instance shuts down and terminates itself."
  aws s3 cp --recursive --only-show-errors "$WORK/results" "${RUN_URI}/results/" || true
  echo "exit=${code}" | aws s3 cp - "${RUN_URI}/DONE" || true
  shutdown -h now
}
trap finish EXIT

log "▶ [2/6] Installing Java (Amazon Corretto 21) with dnf"
dnf install -y -q java-21-amazon-corretto-headless
log "✔ $(java -version 2>&1 | head -1)"

log "▶ [3/6] Copying JMeter ${JMETER_VERSION} from s3://${BUCKET}/tools/"
aws s3 cp --only-show-errors "s3://${BUCKET}/tools/apache-jmeter-${JMETER_VERSION}.tgz" "$WORK/"
tar -xzf "$WORK/apache-jmeter-${JMETER_VERSION}.tgz" -C "$WORK"
log "✔ JMeter unpacked to $WORK/apache-jmeter-${JMETER_VERSION}"

log "▶ [4/6] Copying the test plan from ${RUN_URI}/plan.jmx"
aws s3 cp --only-show-errors "${RUN_URI}/plan.jmx" "$WORK/plan.jmx"
log "✔ Test plan ready"

# bin/jmeter reads its heap from HEAP (default -Xms1g -Xmx1g).
export HEAP="$JMETER_HEAP"

log "▶ [5/6] Running JMeter (non-GUI mode) — this is the actual load test"
"$WORK/apache-jmeter-${JMETER_VERSION}/bin/jmeter" -n \
  -t "$WORK/plan.jmx" \
  -Jthreads="$THREADS" -Jloops="$LOOPS" \
  -l "$WORK/results/results.jtl" \
  -j "$WORK/results/jmeter.log" \
  -e -o "$WORK/results/report"
log "✔ JMeter finished: $(($(wc -l < "$WORK/results/results.jtl") - 1)) requests recorded, HTML report generated"
