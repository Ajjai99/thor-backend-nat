#!/bin/bash
# EC2 user data for one performance-test run, launched by .github/workflows/performance-test.yml. The workflow
# prepends the run's settings as shell variables:
#   BUCKET RUN_ID JMETER_VERSION THREADS LOOPS MAX_MINUTES
# Pulls Java (dnf) and JMeter + the test plan (S3), runs the test, writes results/ and finally DONE to
# s3://$BUCKET/runs/$RUN_ID/, then shuts down — which terminates the instance (launch template setting).
set -euo pipefail

RUN_URI="s3://${BUCKET}/runs/${RUN_ID}"
WORK=/opt/perf
mkdir -p "$WORK/results"
exec > >(tee "$WORK/results/run.log") 2>&1

# Hard cap: the instance terminates after MAX_MINUTES even if JMeter hangs.
shutdown -h "+${MAX_MINUTES}"

finish() {
  local code=$?
  echo "run finished, exit=${code}"
  aws s3 cp --recursive --only-show-errors "$WORK/results" "${RUN_URI}/results/" || true
  echo "exit=${code}" | aws s3 cp - "${RUN_URI}/DONE" || true
  shutdown -h now
}
trap finish EXIT

dnf install -y -q java-21-amazon-corretto-headless

aws s3 cp --only-show-errors "s3://${BUCKET}/tools/apache-jmeter-${JMETER_VERSION}.tgz" "$WORK/"
tar -xzf "$WORK/apache-jmeter-${JMETER_VERSION}.tgz" -C "$WORK"
aws s3 cp --only-show-errors "${RUN_URI}/plan.jmx" "$WORK/plan.jmx"

# Give JMeter ~75% of the instance's memory.
heap_mb=$(awk '/MemTotal/ {printf "%d", $2 * 0.75 / 1024}' /proc/meminfo)
export JVM_ARGS="-Xms${heap_mb}m -Xmx${heap_mb}m"

"$WORK/apache-jmeter-${JMETER_VERSION}/bin/jmeter" -n \
  -t "$WORK/plan.jmx" \
  -Jthreads="$THREADS" -Jloops="$LOOPS" \
  -l "$WORK/results/results.jtl" \
  -j "$WORK/results/jmeter.log" \
  -e -o "$WORK/results/report"
