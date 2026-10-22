#!/bin/bash
# EC2 user data for one performance-test run, launched by .github/workflows/performance-test.yml. The workflow
# prepends the run's settings as shell variables:
#   BUCKET RUN_ID JMETER_VERSION JMETER_HEAP MAX_MINUTES
#   THREAD_COUNT RAMP_UP_SECONDS TEST_DURATION_SECONDS THINK_TIME_MIN_MS THINK_TIME_MAX_MS
# Pulls Java (dnf), JMeter and the test inputs (plan, config, test data, zip-pool) from S3, runs the test, writes
# results/ and finally DONE to s3://$BUCKET/runs/$RUN_ID/, then shuts down — which terminates the instance (launch
# template setting). Everything printed here ends up in results/run.log in S3.
set -euo pipefail

RUN_URI="s3://${BUCKET}/runs/${RUN_ID}"
WORK=/opt/perf
TESTS="$WORK/tests" # same layout as perf-tests/: plan.jmx, config.properties, config/test-data.csv, zip-pool/
mkdir -p "$WORK/results"
exec > >(tee "$WORK/results/run.log") 2>&1

log() { echo "$(date -u +%H:%M:%S) $*"; }

log "▶ [1/6] Instance started for run ${RUN_ID}"
log "  CPUs: $(nproc), memory: $(free -g | awk '/Mem:/ {print $2}') GiB, free disk: $(df -h / | awk 'NR==2 {print $4}')"
log "  Load: ${THREAD_COUNT} users, ${RAMP_UP_SECONDS}s ramp-up, ${TEST_DURATION_SECONDS}s duration," \
  "think time ${THINK_TIME_MIN_MS}-${THINK_TIME_MAX_MS} ms"
log "  JMeter ${JMETER_VERSION}, heap ${JMETER_HEAP}"

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

log "▶ [4/6] Copying the test plan, config, test data and zip-pool from ${RUN_URI}/inputs/"
aws s3 cp --recursive --only-show-errors "${RUN_URI}/inputs/" "$TESTS/"
log "✔ Test inputs ready: $(find "$TESTS/zip-pool" -name '*.zip' | wc -l) ZIPs in the pool"

# bin/jmeter reads its heap from HEAP (default -Xms1g -Xmx1g).
export HEAP="$JMETER_HEAP"

# Run from inside $TESTS: the test plan finds config/test-data.csv and zip-pool/ by relative path.
# -q adds config.properties on top of JMeter's own jmeter.properties (-p would replace it and break the HTML
# report); the -J values are applied last, so they override the same keys in config.properties.
# result.file is the plan's own extra results file; point it into results/ so it is uploaded too.
log "▶ [5/6] Running JMeter (non-GUI mode) — this is the actual load test"
cd "$TESTS"
"$WORK/apache-jmeter-${JMETER_VERSION}/bin/jmeter" -n \
  -t "$TESTS/plan.jmx" \
  -q "$TESTS/config.properties" \
  -Jthread.count="$THREAD_COUNT" \
  -Jramp.up.seconds="$RAMP_UP_SECONDS" \
  -Jtest.duration.seconds="$TEST_DURATION_SECONDS" \
  -Jthink.time.min.ms="$THINK_TIME_MIN_MS" \
  -Jthink.time.max.ms="$THINK_TIME_MAX_MS" \
  -Jresult.file="$WORK/results/plan-results.jtl" \
  -l "$WORK/results/results.jtl" \
  -j "$WORK/results/jmeter.log" \
  -e -o "$WORK/results/report"
log "✔ JMeter finished: $(($(wc -l < "$WORK/results/results.jtl") - 1)) requests recorded, HTML report generated"
