# Infrastructure (Bicep)

One deployment per environment at resource-group scope: `main.bicep` + `params/{dev,test,prod}.bicepparam`.
Nothing here has been deployed or compiled against Azure by the author; run `az bicep build --file infra/main.bicep`
and a `what-if` before the first real deploy. No secret values exist in params or outputs.

## Modules (`modules/`)

| Module | Creates |
|---|---|
| `monitoring` | Log Analytics workspace, workspace-based Application Insights |
| `identity` | User-assigned managed identity (the app identity; `AZURE_CLIENT_ID`) |
| `acr` / `acr-pull` | Shared ACR (dev only); AcrPull for the identity (scope = ACR resource group) |
| `keyvault` / `keyvault-secrets` | Key Vault (RBAC mode); dev/test: Secrets User for the identity, `cosmos-key` and `search-query-key` secrets |
| `cosmos` | Account (serverless dev/test, autoscale prod, `disableLocalAuth` in prod), db `supportchat`, container `chat` (`/userId`, indexing policy, `defaultTtl -1`), data-plane role (prod, test) |
| `search` | AI Search (semantic ranker `standard`, system identity; `aadOrApiKey` dev/test, local auth disabled prod), Search Index Data Reader (prod, test) |
| `container-env` | Container Apps environment |
| `containerapp` | Generic app: used for backend (internal ingress, `/healthz` startup+liveness, `/readyz` readiness, HTTP-concurrency scale rule, min replicas 1 in prod) and frontend (external ingress) |
| `smoke-job` | test only, optional: manual Container Apps Job, `--smoke-check`, `Azure__AuthMode=ManagedIdentity`, no keys (ADR 0003 1a) |
| `ingestion-job` | optional: manual Container Apps Job for the ingestion console app, dedicated identity `id-<base>-ingest` |
| `role-foundry-project` | "Azure AI User" on an existing Foundry project, only if `foundryProjectResourceId` is set |
| `role-model-account` | Search identity -> "Cognitive Services OpenAI User" on the embedding model resource, only if `modelResourceId` is set (Phase 2) |

## Per-environment behaviour

| | dev | test | prod |
|---|---|---|---|
| `ASPNETCORE_ENVIRONMENT` / `Azure__AuthMode` | `Dev` / `Key` | `Test` / `Key` | `Production` / `ManagedIdentity` |
| Cosmos | serverless, keys | serverless, keys + data role for MI | provisioned autoscale, local auth off, data role for MI |
| Search | `aadOrApiKey` | `aadOrApiKey` + Index Data Reader for MI | local auth off, Index Data Reader for MI |
| Key Vault secrets | yes (KV references on the apps) | yes | none (no Secrets User, purge protection on) |
| Min replicas (backend/frontend) | 0 | 0 | 1 |

Foundry: `foundryProjectResourceId` (default empty) -> "Azure AI User" for the identity in every environment once set.
`foundryProjectEndpoint` sets `Foundry__ProjectEndpoint`. Both stay empty in Phase 1 (echo only, D8).

## Shared ACR

One registry (`acrName`), created by the dev deployment (`createAcr = true`). test and prod set `createAcr = false`,
`acrName` and `acrResourceGroup` (the dev RG) and only get an AcrPull assignment for their own identity, created by the
`acr-pull` module at the registry's resource group scope. Apps pull with the managed identity (admin user disabled).
The registry is not mirrored per stage so the same image (same digest) is promoted. If you do not want the infra
pipeline to hold role-assignment rights in the ACR resource group, set `assignAcrPull = false` and create the
assignment manually once.

## Images

`backendImage` / `frontendImage` empty -> a public placeholder image (port 80, no probes) so infra can be deployed
before the first image exists. Real images listen on `containerPort` (8080, non-root; assumption for the Dockerfiles).
CAUTION: re-running the infra deployment with empty image params resets the apps to the placeholder. After the first
real deploy, pass the current images (e.g. `--parameters backendImage=$(az containerapp show ... --query properties.template.containers[0].image -o tsv)`)
or avoid infra redeploys without them. See open questions in the hand-off report.

## App settings params (retention, sign-in, models)

| Param | Env vars | dev / test / prod |
|---|---|---|
| `chatRetentionDays` (int, 1..3650, required) | backend and MI smoke job `Chat__RetentionDays`; frontend `APP_RETENTION_DAYS` | 30 / 30 / 90 (D7; user-changeable defaults) |
| `authProviders` (JSON string; empty = unset) | frontend `AUTH_PROVIDERS` | Google, Facebook and `{"id":"email","label":"Sign in with email"}` / same / Google and Facebook only (D6) |
| `supportContact` (plain text, max 200; empty = unset) | frontend `APP_SUPPORT_CONTACT` | empty / empty / `REPLACE_ME` (caught by `check-params.sh`) |
| `orchestratorDeployment` (empty = unset) | backend and smoke job `Workflows__SupportTicket__Agents__Orchestrator__Deployment` | user input (D8) |
| `specialistDeployment` (empty = unset) | backend and smoke job `Workflows__SupportTicket__Agents__{Policy,Claims,HealthAssessments}__Deployment` | user input (D8) |

The Cosmos container keeps `defaultTtl -1` (TTL on, no default expiry); the app writes the per-document `ttl`
(data-model 1.3). Changing `chatRetentionDays` only affects documents written afterwards.

## User-supplied inputs per environment

Set in `params/<env>.bicepparam` (or `--parameters`); nothing here is a secret.
- Entra: `entraInstance`, `entraTenantId`, `apiClientId`, `spaClientId`, `spaAuthority`, `apiScope` (`docs/auth-setup.md`).
- Foundry (D8): `foundryProjectResourceId`, `foundryProjectEndpoint`, `modelResourceId`, `embeddingDeployment`
  (the `text-embedding-3-small` deployment name), `orchestratorDeployment` and `specialistDeployment` (chat deployment names).
- Real resource names (R1): at least a globally unique `acrName` (dev creates it; test/prod reference it via `acrResourceGroup`).
- Prod: `supportContact`; review the `chatRetentionDays` defaults in all three files.

Infra is deployed from the user's machine in v1 (D11): create the OIDC identities with `scripts/setup-github-oidc.sh`
without `--infra-roles`, so the pipeline identities never hold role-assignment rights.

## Placeholder guard
Entra params (`entraInstance`, `entraTenantId`, `apiClientId`, `spaClientId`, `spaAuthority`, `apiScope`) have no defaults in
`main.bicep`. The committed `params/*.bicepparam` hold `REPLACE_ME` / all-zero GUID placeholders so they compile. Bicep
`assert` is experimental in 0.48 (BCP349) and is not enabled, so `scripts/check-params.sh` greps the param files and exits 1
on any placeholder (including prod `supportContact = 'REPLACE_ME'`). The chat deployment params `orchestratorDeployment` and
`specialistDeployment` are `'REPLACE_ME'` in all three files until filled in (D8), so they are caught too; the script also
fails if they (or `embeddingDeployment`, when `deployIngestionJob = true`) are set to `''`. The backend additionally rejects
`<...>`/`REPLACE_ME` deployment names at startup. The infra pipeline (Phase 4) must run it before `what-if` / deploy.

## Deploy order

1. Manual prerequisites: resource group per environment, the GitHub OIDC identities (`docs/cicd-setup.md`), Entra External
   ID registrations (`docs/auth-setup.md`) -> fill `entra*`, `apiClientId`, `spa*`, `apiScope` in the param files.
2. Deploy dev first (it creates the shared ACR), then test and prod:
   `az deployment group create -g <rg> --parameters infra/params/<env>.bicepparam` (run by the user or the infra workflow).
   First deploy writes `cosmos-key` and `search-query-key` to Key Vault (dev/test) before the apps reference them.
   If it fails right after the role assignment (Key Vault Secrets User / AcrPull propagation), re-run it.
3. Search query key: Bicep reads the default query key with `listQueryKeys()` (`writeSearchQueryKeyFromBicep=true`), so
   no extra step is needed. Fallback only if you set it to `false` or the default key was deleted:
   `infra/scripts/ensure-search-query-key.sh <rg> <search-name> <key-vault-name>` (run by the user; never prints the key).
   The names come from the deployment outputs.
4. App revisions: the deploy workflow (`az containerapp update --image ... --revision-suffix ...`) rolls the real images.
5. Search index creation / ingestion: the ingestion job (see below).
6. Test: after the backend image supports `--smoke-check`, set `deploySmokeJob = true` and pass `backendImage`.

## Ingestion job

Params: `deployIngestionJob` (default false), `ingestionImage` (`<acr>/support-ingestion:<tag>`, empty = no job),
`embeddingDeployment`, `modelResourceId` (or `vectorizerResourceUri`), `foundryProjectEndpoint`, `foundryProjectResourceId`,
`ingestionSourcePath` (default `/app/knowledge`; `Dockerfile.ingestion` must copy `knowledge/` there).
Creates job `caj-<base>-ingest` (manual trigger, 0.5 cpu / 1Gi, replicaTimeout 1800 s, no retry) with a dedicated identity:

| Env | Mode | Identity roles |
|---|---|---|
| dev/test | `Azure__AuthMode=Key`; `Ingestion__SearchAdminKey` = Key Vault secret `search-admin-key` (written by Bicep via `listAdminKeys()`) | AcrPull, Key Vault Secrets User, Azure AI User on the Foundry project, OpenAI User on the model account (the last two only if the ids are set) |
| prod | `ManagedIdentity`, no keys, no admin-key secret | AcrPull, Search Index Data Contributor + Search Service Contributor on the Search service, Azure AI User on the project, OpenAI User on the model account |

The API identity never gets Contributor roles on Search or the admin key. Env passed to the job: `ASPNETCORE_ENVIRONMENT`,
`DOTNET_ENVIRONMENT`, `AZURE_CLIENT_ID` (ingestion identity), `AZURE_TOKEN_CREDENTIALS`, `Azure__AuthMode`, `Search__Endpoint`,
`Search__IndexName`, `Ingestion__SourcePath`, `Ingestion__EmbeddingDeployment`, `Ingestion__VectorizerDeployment`,
`Ingestion__VectorizerResourceUri` (from `vectorizerResourceUri` or `https://<model account>.openai.azure.com`),
`Foundry__ProjectEndpoint`, and (dev/test) `Ingestion__SearchAdminKey` (secretRef). The app fails fast when the endpoint or
vectorizer uri are missing, so set those params first.

Start manually (after the image is pushed): `az containerapp job start -g <rg> -n <ingestionJobName>`; follow with
`az containerapp job execution list -g <rg> -n <ingestionJobName>`. First-deploy order: infra with `deployIngestionJob=false`
-> build/push `support-ingestion` -> redeploy infra with `deployIngestionJob=true` and `ingestionImage` (also pass the current
backend/frontend images) -> wait for role propagation -> start the job -> deploy/verify the backend. The search service identity
needs OpenAI User on the model account (`modelResourceId`) for the integrated vectorizer. Re-run the infra deploy if the first
attempt fails on Key Vault / role propagation.

## Outputs for pipelines

`resourceGroup`, `acrName`, `acrLoginServer`, `backendAppName`, `frontendAppName`, `frontendFqdn`,
`managedIdentityClientId`, plus `keyVaultName`, `cosmosAccountName`, `searchServiceName`, `searchPrincipalId`,
`smokeJobName` (empty unless the test smoke job exists), `ingestionJobName` (empty unless the ingestion job exists). Map to GitHub environment vars: `AZURE_RESOURCE_GROUP`,
`ACR_NAME`, `BACKEND_APP`, `FRONTEND_APP` (`MI_SMOKE_JOB` in test, `INGESTION_JOB` for ingestion). `AZURE_CLIENT_ID` of the pipeline is the
GitHub OIDC identity, NOT `managedIdentityClientId` (that is the app identity).

## Roles for the pipeline identity (per `github-azure-oidc-setup`; granted by the user)

- Deploy workflows (all environments): `Contributor` on the environment RG (update apps, start the smoke job).
- Dev identity: `AcrPush` on the shared ACR (build-push).
- Infra workflow (this folder) additionally needs, on the environment RG: `Role Based Access Control Administrator`
  (or `User Access Administrator`) to create role assignments (Key Vault, Search, ACR, Cosmos data roles use
  Cosmos-specific assignments and are covered by Contributor), and `Key Vault Secrets Officer` to write the
  dev/test secrets (the vault uses RBAC). For test/prod `acr-pull`: role-assignment rights on the ACR scope
  (or `assignAcrPull = false`). For Foundry/model role assignments: role-assignment rights on that project/account.
- Contributor alone is NOT enough for `infra.yml`. Deploying infra from a laptop as the user avoids granting the
  pipeline these roles (suggested for v1; the user decides).

## Manual / out of scope

- Entra External ID tenant, app registrations, Google/Facebook federation, redirect URIs: `docs/auth-setup.md`.
- Foundry project and model deployments (referenced only).
- GitHub environments, OIDC identities, variables: `docs/cicd-setup.md`.
