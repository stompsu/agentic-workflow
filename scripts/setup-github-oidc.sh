#!/usr/bin/env bash
# One-time setup of GitHub Environments + Azure federated credentials (OIDC) for the pipelines.
# RUN BY THE USER. Default is --dry-run: prints every command and executes nothing.
# Pass --apply to execute. Prerequisites: az login (Owner/UAA on the scopes below), gh auth login (repo admin).
#
# Usage:
#   scripts/setup-github-oidc.sh --org ORG --repo REPO --acr ACR_NAME --acr-rg ACR_RG \
#       --rg-dev RG --rg-test RG --rg-prod RG [--envs "dev test prod"] [--infra-roles] [--apply]
#
# Idempotent where possible: existing identities / federated credentials are detected and skipped,
# role assignments and `gh variable set` are naturally repeatable.
# GitHub Environments: the script only CREATES a missing environment (PUT .../environments/{env}, "Create or update").
# An existing environment is never touched, because the REST docs do not say what happens to omitted fields such as
# `reviewers` on update, and PUT semantics may reset them (https://docs.github.com/en/rest/deployments/environments).
# For existing environments check required reviewers and the branch policy in the portal (docs/cicd-setup.md section 2).
# Required reviewers are never set by the script (needs user ids): do that in the portal.
# Resource groups must already exist.
set -euo pipefail

ORG=""; REPO=""; ACR=""; ACR_RG=""
RG_dev=""; RG_test=""; RG_prod="" # bash 3.2 (macOS) has no associative arrays
ENVS="dev test prod"
INFRA_ROLES=false
APPLY=false

usage() { sed -n '2,8p' "$0"; exit "${1:-0}"; }

# Resource group for an environment name (explicit lookup instead of indirect expansion; empty if unknown).
rg_for() {
  case "$1" in
    dev) printf '%s' "$RG_dev" ;;
    test) printf '%s' "$RG_test" ;;
    prod) printf '%s' "$RG_prod" ;;
    *) printf '' ;;
  esac
}

while [ $# -gt 0 ]; do
  case "$1" in
    --org) ORG="$2"; shift 2 ;;
    --repo) REPO="$2"; shift 2 ;;
    --acr) ACR="$2"; shift 2 ;;
    --acr-rg) ACR_RG="$2"; shift 2 ;;
    --rg-dev) RG_dev="$2"; shift 2 ;;
    --rg-test) RG_test="$2"; shift 2 ;;
    --rg-prod) RG_prod="$2"; shift 2 ;;
    --envs) ENVS="$2"; shift 2 ;;
    --infra-roles) INFRA_ROLES=true; shift ;;
    --apply) APPLY=true; shift ;;
    --dry-run) APPLY=false; shift ;;
    -h|--help) usage 0 ;;
    *) echo "Unknown argument: $1" >&2; usage 1 ;;
  esac
done

[ -n "$ORG" ] && [ -n "$REPO" ] && [ -n "$ACR" ] && [ -n "$ACR_RG" ] || { echo "Missing --org/--repo/--acr/--acr-rg" >&2; usage 1; }
for e in $ENVS; do
  [ -n "$(rg_for "$e")" ] || { echo "Missing --rg-$e (or unknown environment)" >&2; exit 1; }
done

if $APPLY; then echo "MODE: APPLY (commands will be executed)"; else echo "MODE: DRY-RUN (nothing is executed; pass --apply to run)"; fi
echo "Repo: $ORG/$REPO   Shared ACR: $ACR (rg $ACR_RG)   Environments: $ENVS   Infra roles: $INFRA_ROLES"

# Print the command; execute only with --apply.
run() { printf '+'; printf ' %q' "$@"; printf '\n'; if $APPLY; then "$@"; fi; }
# Execute-and-capture helper: in dry-run returns the placeholder given as $1.
capture() { local placeholder="$1"; shift; { printf '+'; printf ' %q' "$@"; printf '\n'; } >&2; if $APPLY; then "$@"; else echo "$placeholder"; fi; }

if $APPLY; then
  SUB_ID=$(az account show --query id -o tsv)
  TENANT_ID=$(az account show --query tenantId -o tsv)
else
  SUB_ID="<subscription-id>"; TENANT_ID="<tenant-id>"
  echo "# SUB_ID / TENANT_ID are read from 'az account show' with --apply"
fi
ACR_ID=$(capture "<acr-resource-id>" az acr show -n "$ACR" -g "$ACR_RG" --query id -o tsv)

# OIDC subject prefix as GitHub presents it. Repos with immutable subjects send "repo:OWNER@ID/REPO@ID", others
# "repo:OWNER/REPO" (https://docs.github.com/en/rest/actions/oidc). Read-only GET, also in dry-run.
SUB_PREFIX="repo:$ORG/$REPO"
OIDC_SUB_API="repos/$ORG/$REPO/actions/oidc/customization/sub"
if USE_DEFAULT=$(gh api "$OIDC_SUB_API" --jq '.use_default' 2>/dev/null); then
  if [ "$USE_DEFAULT" != "true" ]; then
    echo "Repo uses custom OIDC claim keys: this script supports the default subject format only (gh api $OIDC_SUB_API)." >&2
    exit 1
  fi
  SUB_PREFIX=$(gh api "$OIDC_SUB_API" --jq ".sub_claim_prefix // \"$SUB_PREFIX\"")
elif $APPLY; then
  echo "Cannot read $OIDC_SUB_API (gh auth login, repo admin)" >&2
  exit 1
else
  echo "# (dry-run: could not read the OIDC subject prefix from GitHub; assuming $SUB_PREFIX)"
fi
echo "# OIDC subject prefix: $SUB_PREFIX"

for ENV in $ENVS; do
  RG="$(rg_for "$ENV")"
  ID_NAME="id-github-$ENV"
  echo
  echo "=== Environment: $ENV (resource group $RG) ==="

  echo "# 1. GitHub Environment, restricted to deployments from main (created only when missing)"
  if $APPLY; then
    if gh api "repos/$ORG/$REPO/environments/$ENV" >/dev/null 2>&1; then
      env_exists=true
    else
      env_exists=false
    fi
  else
    env_exists=false
    echo "# (with --apply: GET repos/$ORG/$REPO/environments/$ENV first; if it exists nothing below in step 1 is executed)"
    [ "${DRYRUN_ASSUME_EXISTING:-}" = "$ENV" ] && env_exists=true
  fi
  if $env_exists; then
    echo "# environment $ENV already exists: NOT modified. Check in the portal that required reviewers and the"
    echo "# deployment branch policy (selected branches = main) are as intended (docs/cicd-setup.md section 2)."
  else
    run gh api -X PUT "repos/$ORG/$REPO/environments/$ENV" \
      -F "deployment_branch_policy[protected_branches]=false" \
      -F "deployment_branch_policy[custom_branch_policies]=true"
    run gh api -X POST "repos/$ORG/$REPO/environments/$ENV/deployment-branch-policies" -f name=main
  fi
  if [ "$ENV" != "dev" ]; then
    echo "# MANUAL (portal): Settings > Environments > $ENV > Required reviewers."
  fi

  echo "# 2. User-assigned managed identity + federated credential (subject: $SUB_PREFIX:environment:$ENV)"
  if $APPLY && az identity show -g "$RG" -n "$ID_NAME" >/dev/null 2>&1; then
    echo "# identity $ID_NAME exists, skipping create"
  else
    run az identity create -g "$RG" -n "$ID_NAME"
  fi
  SUBJECT="$SUB_PREFIX:environment:$ENV"
  EXISTING_SUBJECT=""
  if $APPLY; then
    EXISTING_SUBJECT=$(az identity federated-credential show --identity-name "$ID_NAME" -g "$RG" --name "github-$ENV" \
      --query subject -o tsv 2>/dev/null || true)
  fi
  if [ "$EXISTING_SUBJECT" = "$SUBJECT" ]; then
    echo "# federated credential github-$ENV exists with subject $SUBJECT, skipping"
  else
    # Create, or fix the subject of an existing credential (e.g. after the repo switched to immutable subjects).
    [ -n "$EXISTING_SUBJECT" ] && verb=update || verb=create
    [ -n "$EXISTING_SUBJECT" ] && echo "# federated credential github-$ENV has subject $EXISTING_SUBJECT: updating"
    run az identity federated-credential "$verb" --name "github-$ENV" --identity-name "$ID_NAME" -g "$RG" \
      --issuer https://token.actions.githubusercontent.com \
      --subject "$SUBJECT" \
      --audiences api://AzureADTokenExchange
  fi
  CLIENT_ID=$(capture "<client-id-of-$ID_NAME>" az identity show -g "$RG" -n "$ID_NAME" --query clientId -o tsv)
  PRINCIPAL_ID=$(capture "<principal-id-of-$ID_NAME>" az identity show -g "$RG" -n "$ID_NAME" --query principalId -o tsv)
  RG_ID=$(capture "<rg-id-of-$RG>" az group show -n "$RG" --query id -o tsv)

  echo "# 3. Roles (identity may need a minute to replicate before role assignment succeeds; re-run if it fails)"
  run az role assignment create --assignee-object-id "$PRINCIPAL_ID" --assignee-principal-type ServicePrincipal \
    --role Contributor --scope "$RG_ID"
  if [ "$ENV" = "dev" ]; then
    run az role assignment create --assignee-object-id "$PRINCIPAL_ID" --assignee-principal-type ServicePrincipal \
      --role AcrPush --scope "$ACR_ID"
  fi
  if $INFRA_ROLES; then
    echo "# Infra workflow roles (infra/README.md): create role assignments (+ write Key Vault secrets outside prod)"
    run az role assignment create --assignee-object-id "$PRINCIPAL_ID" --assignee-principal-type ServicePrincipal \
      --role "Role Based Access Control Administrator" --scope "$RG_ID"
    if [ "$ENV" != "prod" ]; then
      echo "# Key Vault secrets (cosmos-key, search-query-key, search-admin-key) exist only in dev/test; prod uses Managed Identity"
      run az role assignment create --assignee-object-id "$PRINCIPAL_ID" --assignee-principal-type ServicePrincipal \
        --role "Key Vault Secrets Officer" --scope "$RG_ID"
    fi
    if [ "$ENV" != "dev" ]; then
      echo "# acr-pull module assigns AcrPull at the ACR scope: grant role-assignment rights there (or set assignAcrPull=false)"
      run az role assignment create --assignee-object-id "$PRINCIPAL_ID" --assignee-principal-type ServicePrincipal \
        --role "Role Based Access Control Administrator" --scope "$ACR_ID"
    fi
  fi

  echo "# 4. GitHub environment variables (not secrets). Names of apps/jobs come from the Bicep outputs after the first infra deploy."
  run gh variable set AZURE_CLIENT_ID --env "$ENV" --repo "$ORG/$REPO" --body "$CLIENT_ID"
  run gh variable set AZURE_TENANT_ID --env "$ENV" --repo "$ORG/$REPO" --body "$TENANT_ID"
  run gh variable set AZURE_SUBSCRIPTION_ID --env "$ENV" --repo "$ORG/$REPO" --body "$SUB_ID"
  run gh variable set AZURE_RESOURCE_GROUP --env "$ENV" --repo "$ORG/$REPO" --body "$RG"
  run gh variable set ACR_NAME --env "$ENV" --repo "$ORG/$REPO" --body "$ACR"
  echo "# Set after infra is deployed (deployment outputs backendAppName, frontendAppName, smokeJobName, ingestionJobName):"
  echo "#   gh variable set BACKEND_APP  --env $ENV --repo $ORG/$REPO --body <backendAppName>"
  echo "#   gh variable set FRONTEND_APP --env $ENV --repo $ORG/$REPO --body <frontendAppName>"
  [ "$ENV" = "test" ] && echo "#   gh variable set MI_SMOKE_JOB --env $ENV --repo $ORG/$REPO --body <smokeJobName>"
  echo "#   gh variable set INGESTION_JOB --env $ENV --repo $ORG/$REPO --body <ingestionJobName>   (optional)"
done

echo
if $APPLY; then echo "Done."; else echo "Dry run finished. Review the commands above, then re-run with --apply."; fi
