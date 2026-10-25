#!/usr/bin/env bash
# Waits for a Container Apps Job execution to finish. Exit 0 only on Succeeded.
# Usage: wait-job-execution.sh <job-name> <resource-group> <execution-name> [timeout-seconds]
set -euo pipefail
job="$1"; rg="$2"; exec_name="$3"; timeout="${4:-600}"
deadline=$((SECONDS + timeout))
while :; do
  status=$(az containerapp job execution show -n "$job" -g "$rg" --job-execution-name "$exec_name" \
    --query properties.status -o tsv)
  echo "Execution $exec_name status: $status"
  case "$status" in
    Succeeded) exit 0 ;;
    Failed|Stopped|Degraded) echo "::error::Job execution $exec_name ended with status $status"; exit 1 ;;
  esac
  if [ "$SECONDS" -ge "$deadline" ]; then
    echo "::error::Timed out after ${timeout}s waiting for $exec_name (last status: $status)"; exit 1
  fi
  sleep 10
done
