#!/usr/bin/env bash
# Fails (exit 1) if any .bicepparam still holds placeholder values. Run by the infra pipeline before deploy.
# Usage: infra/scripts/check-params.sh [file.bicepparam ...]   (default: infra/params/<env>.bicepparam for all envs)
set -euo pipefail
dir="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
files=("$@"); [ ${#files[@]} -eq 0 ] && files=("$dir"/params/*.bicepparam)
bad=0
for f in "${files[@]}"; do
  if grep -nE "REPLACE_ME|00000000-0000-0000-0000-000000000000" "$f" | grep -vE '^[0-9]+:[[:space:]]*//'; then
    echo "ERROR: placeholder values in $f" >&2; bad=1
  fi
  # Deployment names must not be empty (embeddingDeployment only matters when the ingestion job is enabled).
  req="orchestratorDeployment specialistDeployment"
  grep -qE "^[[:space:]]*param[[:space:]]+deployIngestionJob[[:space:]]*=[[:space:]]*true" "$f" && req="$req embeddingDeployment"
  for p in $req; do
    if grep -qE "^[[:space:]]*param[[:space:]]+${p}[[:space:]]*=[[:space:]]*''" "$f"; then
      echo "ERROR: $p is empty in $f" >&2; bad=1
    fi
  done
done
[ $bad -eq 0 ] && echo "check-params: OK"
exit $bad
