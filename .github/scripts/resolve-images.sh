#!/usr/bin/env bash
# Reads the images currently deployed to the environment's apps/job and writes
# `extra_params=backendImage=... frontendImage=...` to $GITHUB_OUTPUT (single line, no newline hazards).
# Only real ACR images are passed back; empty on first deploy. Needs RG, ACR, BACKEND_APP, FRONTEND_APP, INGESTION_JOB.
set -euo pipefail
az config set extension.use_dynamic_install=yes_without_prompt
PARAMS=""
add() { # name, image
  case "$2" in
    "$ACR.azurecr.io/"*) PARAMS="$PARAMS $1=$2"; echo "Keeping $1=$2" ;;
    *) echo "No deployed ACR image for $1 (got '$2'); leaving it to the template default" ;;
  esac
}
B=$(az containerapp show -n "$BACKEND_APP" -g "$RG" --query 'properties.template.containers[0].image' -o tsv 2>/dev/null || true)
F=$(az containerapp show -n "$FRONTEND_APP" -g "$RG" --query 'properties.template.containers[0].image' -o tsv 2>/dev/null || true)
add backendImage "$B"
add frontendImage "$F"
if [ -n "${INGESTION_JOB:-}" ]; then
  I=$(az containerapp job show -n "$INGESTION_JOB" -g "$RG" --query 'properties.template.containers[0].image' -o tsv 2>/dev/null || true)
  add ingestionImage "$I"
fi
echo "extra_params=${PARAMS# }" >> "$GITHUB_OUTPUT"
