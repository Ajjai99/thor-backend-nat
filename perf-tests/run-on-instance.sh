#!/bin/bash
# EC2 user data for one performance-test run, launched by .github/workflows/performance-test.yml. The workflow
# prepends the run's settings as shell variables:
#   BUCKET RUN_ID JMETER_VERSION JMETER_HEAP MAX_MINUTES RUN_SCRIPT
#   THREAD_COUNT RAMP_UP_SECONDS TEST_DURATION_SECONDS THINK_TIME_MIN_MS THINK_TIME_MAX_MS
# Pulls Java (dnf), JMeter and the test inputs (run script, plan, config, test data, zip-pool) from S3, runs the
# run script, writes results/ and finally DONE to s3://$BUCKET/runs/$RUN_ID/, then shuts down — which terminates the
# instance (launch template setting). Everything printed here ends up in results/run.log in S3.
set -euo pipefail

RUN_URI="s3://${BUCKET}/runs/${RUN_ID}"
WORK=/opt/perf
TESTS="$WORK/tests" # same layout as perf-tests/: the run script, plan, config/, zip-pool/
mkdir -p "$WORK/results"
exec > >(tee "$WORK/results/run.log") 2>&1

log() { echo "$(date -u +%H:%M:%S) $*"; }

log "▶ [1/6] Instance started for run ${RUN_ID}"
log "  CPUs: $(nproc), memory: $(free -g | awk '/Mem:/ {print $2}') GiB, free disk: $(df -h / | awk 'NR==2 {print $4}')"
log "  Load: ${THREAD_COUNT} users, ${RAMP_UP_SECONDS}s ramp-up, ${TEST_DURATION_SECONDS}s duration," \
  "think time ${THINK_TIME_MIN_MS}-${THINK_TIME_MAX_MS} ms"
log "  JMeter ${JMETER_VERSION}, heap ${JMETER_HEAP}, run script ${RUN_SCRIPT}"

# Hard cap: the instance terminates after MAX_MINUTES even if JMeter hangs.
shutdown -h "+${MAX_MINUTES}"
log "  Safety timer set: the instance terminates in ${MAX_MINUTES} min no matter what"

# The run script writes timestamped files to results/ (e.g. dryrun_<time>.jtl); give the main ones the fixed names
# the workflow's summary and report.zip use, and keep everything else as is.
collect_results() {
  local f
  for f in "$TESTS"/results/*; do
    [[ -e "$f" ]] || continue
    case "$(basename "$f")" in
      report_*|*_report_*) mv "$f" "$WORK/results/report" ;; # report_<time>/, dryrun_report_<time>/ → report/
      *_[0-9]*.jtl) mv "$f" "$WORK/results/results.jtl" ;; # dryrun_<time>.jtl     → results.jtl
      *_[0-9]*.log) mv "$f" "$WORK/results/jmeter.log" ;;  # dryrun_<time>.log     → jmeter.log
      *)            mv "$f" "$WORK/results/" ;;            # e.g. the plan's own dryrun.jtl
    esac
  done
}

# The report is index.html plus its content/ and sbadmin2-*/ folders; the S3 console can't download a folder, so
# also ship it as one zip (Python's zipfile is already on Amazon Linux).
zip_report() {
  [[ -d "$WORK/results/report" ]] || return 0
  (cd "$WORK/results" && python3 -m zipfile -c report.zip report/)
  log "  Packed the HTML report into results/report.zip ($(du -h "$WORK/results/report.zip" | cut -f1))"
}

finish() {
  local code=$?
  log "▶ [6/6] Finishing (exit code ${code})"
  collect_results || true
  zip_report || true
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

log "▶ [4/6] Copying the run script, test plan, config, test data and zip-pool from ${RUN_URI}/inputs/"
aws s3 cp --recursive --only-show-errors "${RUN_URI}/inputs/" "$TESTS/"
log "✔ Test inputs ready: $(find "$TESTS/zip-pool" -name '*.zip' | wc -l) ZIPs in the pool"

# bin/jmeter reads its heap from HEAP (default -Xms1g -Xmx1g); the run script calls `jmeter` from the PATH.
export HEAP="$JMETER_HEAP"
export PATH="$WORK/apache-jmeter-${JMETER_VERSION}/bin:$PATH"

# Run from inside $TESTS: the run script and the test plan find their files by relative path. The -J values are the
# workflow inputs; the script passes them to JMeter, where they override the same keys in its config file.
log "▶ [5/6] Running ${RUN_SCRIPT} — this is the actual load test"
cd "$TESTS"
bash "./${RUN_SCRIPT}" \
  -Jthread.count="$THREAD_COUNT" \
  -Jramp.up.seconds="$RAMP_UP_SECONDS" \
  -Jtest.duration.seconds="$TEST_DURATION_SECONDS" \
  -Jthink.time.min.ms="$THINK_TIME_MIN_MS" \
  -Jthink.time.max.ms="$THINK_TIME_MAX_MS"
log "✔ ${RUN_SCRIPT} finished"
