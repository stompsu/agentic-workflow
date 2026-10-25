#!/usr/bin/env bash
# Fallback for dev/test: ensure a Search QUERY key exists and store it in Key Vault secret `search-query-key`.
# Normally NOT needed: main.bicep writes the default query key itself (writeSearchQueryKeyFromBicep=true).
# Use this only when that param is false (or the default query key was deleted).
#
# Idempotent: reuses the key named supportchat-api, creates it only if absent; skips the Key Vault write when the
# secret already exists unless FORCE=1. The key value is never printed or put on a command line.
# Needs: az login; "Search Service Contributor" (list/create query keys) on the Search service;
#        "Key Vault Secrets Officer" on the vault. Run by the user, not by pipelines.
#
# Usage: ensure-search-query-key.sh <resource-group> <search-service-name> <key-vault-name>
set -euo pipefail

if [[ $# -ne 3 ]]; then
  echo "usage: $0 <resource-group> <search-service-name> <key-vault-name>" >&2
  exit 2
fi
RG="$1"; SEARCH="$2"; VAULT="$3"
KEY_NAME="supportchat-api"
SECRET_NAME="search-query-key"

if [[ "${FORCE:-0}" != "1" ]] && az keyvault secret show --vault-name "$VAULT" --name "$SECRET_NAME" --query id -o tsv >/dev/null 2>&1; then
  echo "Secret '$SECRET_NAME' already exists in '$VAULT'. Nothing to do (FORCE=1 to overwrite)."
  exit 0
fi

umask 077
TMP="$(mktemp)"
TMP2="$TMP.v"
trap 'rm -f "$TMP" "$TMP2"' EXIT

# Reuse the named key if present, otherwise create it. Output goes to the temp file only.
az search query-key list --resource-group "$RG" --service-name "$SEARCH" \
  --query "[?name=='$KEY_NAME'] | [0].key" -o tsv > "$TMP"
if [[ ! -s "$TMP" || "$(tr -d '[:space:]' < "$TMP")" == "None" ]]; then
  az search query-key create --resource-group "$RG" --service-name "$SEARCH" --name "$KEY_NAME" \
    --query key -o tsv > "$TMP"
fi
if [[ ! -s "$TMP" ]]; then
  echo "Could not obtain a query key for '$SEARCH'." >&2
  exit 1
fi

tr -d '\r\n' < "$TMP" > "$TMP2"   # strip the trailing newline from tsv output
az keyvault secret set --vault-name "$VAULT" --name "$SECRET_NAME" --file "$TMP2" --encoding utf-8 --output none
echo "Stored query key '$KEY_NAME' as secret '$SECRET_NAME' in '$VAULT'."
