# SupportChat backend

.NET 10 minimal API. Contract: `/docs/api-contract.md`; decisions: `/docs/adr/`. Everything is stateless
(sessions live in Cosmos DB); user identity is the `oid` claim of the validated token.

```
dotnet build src/backend
dotnet test src/backend
dotnet run --project src/backend/src/SupportChat.Api      # ASPNETCORE_ENVIRONMENT=Development, http://localhost:5080
docker build -t support-backend src/backend               # image support-backend, non-root, port 8080
```

Layout: `src/SupportChat.Core` (contracts), `.Infrastructure` (Cosmos stores, Azure clients, `ChatClientFactory`),
`.Agents` (workflow registry), `.Api` (endpoints, auth). Tests in `tests/`.

## Local configuration (Development: `Azure:AuthMode=Key`)

Secrets are never committed. Set them with user-secrets (the Api project has a `UserSecretsId`):

```
cd src/backend/src/SupportChat.Api
dotnet user-secrets set "Cosmos:Endpoint"   "https://<account>.documents.azure.com:443/"
dotnet user-secrets set "Cosmos:Key"        "<primary key>"
dotnet user-secrets set "Search:Endpoint"   "https://<service>.search.windows.net"
dotnet user-secrets set "Search:Key"        "<QUERY key, not the admin key>"
dotnet user-secrets set "AzureAd:Instance"  "https://<tenant-subdomain>.ciamlogin.com/"
dotnet user-secrets set "AzureAd:TenantId"  "<tenant guid>"
dotnet user-secrets set "AzureAd:ClientId"  "<API app registration client id>"
# SupportTicket is always registered, so these are required whenever the app runs (startup validation rule 7).
# appsettings.json ships <...> placeholders for the four Deployment keys; any value containing '<', '>' or REPLACE_ME
# (case-insensitive) fails startup in every environment, so set real deployment names:
dotnet user-secrets set "Foundry:ProjectEndpoint" "https://<res>.services.ai.azure.com/api/projects/<project>"
dotnet user-secrets set "Workflows:SupportTicket:Agents:Orchestrator:Deployment"       "<chat deployment name>"
dotnet user-secrets set "Workflows:SupportTicket:Agents:Policy:Deployment"             "<chat deployment name>"
dotnet user-secrets set "Workflows:SupportTicket:Agents:Claims:Deployment"             "<chat deployment name>"
dotnet user-secrets set "Workflows:SupportTicket:Agents:HealthAssessments:Deployment"  "<chat deployment name>"
# optional: Workflows:SupportTicket:ConfidenceThreshold (0.7), :MaxConsecutiveClarifications (2),
#           :Agents:<Key>:Temperature
```

Optional chat limits live under `Chat:*` (plan.md section 5).

Retention and "delete my data" (D7, ADR 0004):
- `Chat:RetentionDays` (integer 1..3650): every message and session document gets `ttl = days * 86400` (the container
  has `defaultTtl = -1`). Required in every environment except `Development`; startup fails otherwise (ADR 0003
  rule 9). Deployed values come from the Bicep param `chatRetentionDays` (`Chat__RetentionDays`; defaults dev 30,
  test 30, prod 90). In `Development` leave it unset (no `ttl`, nothing expires) or set it with user-secrets:
  `dotnet user-secrets set "Chat:RetentionDays" "30"`.
- `Chat:DeleteUserDataBudget` (`00:00:05`..`00:03:00`, default `00:01:00`, rule 10): time budget of
  `DELETE /api/me/data`; when it runs out the endpoint answers `503 deletion_incomplete` + `Retry-After: 1` and the
  client repeats the call (progress is kept; api-contract 2.8, data-model 1.7). Ingestion keys: see `src/SupportChat.Ingestion`.

Foundry is always accessed with a `TokenCredential` (`az login` locally); there is no model key.
Deployed environments (`Dev`, `Test`, `Production`) set the same keys as environment variables with `__`
(for example `Cosmos__Key` from a Key Vault reference). `Production` must use `Azure__AuthMode=ManagedIdentity`
with `AZURE_CLIENT_ID` and no `Cosmos__Key` / `Search__Key` at all; the app refuses to start otherwise
(ADR 0003 section 4).

## LocalHost (local mode, no Azure)

`src/SupportChat.LocalHost` is a separate executable that runs the same `/api` pipeline on a laptop with in-memory
services (ADR 0005). It is not referenced by any other project, not published by `Dockerfile`, and excluded in `.dockerignore`.
Run it with `node scripts/run-local.mjs` from the repo root (starts the frontend too), or only the backend:
```bash
dotnet run --project src/backend/src/SupportChat.LocalHost   # http://localhost:5080, Development
```
- Platform: `InMemoryChatStore` (sessions + messages, process lifetime), `InMemoryKnowledgeSearch` (BM25 over the repo
  `knowledge/**/*.md`, copied to the output; chunk ids equal the Azure index), `LocalReadinessCheck`.
- `LocalMode:Models`: `Scripted` (default, deterministic, no network) or `Foundry` (real `ChatClientFactory`,
  `DefaultAzureCredential`, user-secrets `Foundry:ProjectEndpoint` + deployments; sends local chats to Foundry).
- `LocalMode:Auth`: `Local` (default) issues ES256 JWTs from an in-memory key via `POST /local-auth/token {"user":"alice"}`
  (outside `/api`, local only; a restart invalidates all tokens) and validates them like any bearer token; `Entra` uses the
  production Entra validation with `AzureAd:*` from user-secrets.
- Other keys: `LocalMode:LocalAuth:TokenLifetime` (01:00:00), `LocalMode:Scripted:TokenDelay` (00:00:00.030),
  `LocalMode:Knowledge:SourcePath` (`knowledge`), `AllowedHosts` (`localhost;127.0.0.1;[::1]`).
- Guards (startup fails otherwise): Development only; `LocalMode:Enabled=true`; none of `CONTAINER_APP_NAME`,
  `CONTAINER_APP_REVISION`, `CONTAINER_APP_JOB_NAME`, `CONTAINER_APP_ENV_DNS_SUFFIX`, `WEBSITE_SITE_NAME`,
  `KUBERNETES_SERVICE_HOST`, `DOTNET_RUNNING_IN_CONTAINER` set; loopback-only URLs (`*_PORTS` are rejected, checked before
  `Build()` and again after start); loopback-only `AllowedHosts`; `--smoke-check` refused.
- Config: the Api `appsettings.json` is linked as the base (`appsettings.Api.json`), then the LocalHost's own
  `appsettings*.json`. The content root is the output directory.

## Tests

`dotnet test src/backend` runs everything offline: Api.Tests uses the real JWT validation with a test signing key,
the real endpoints and an in-memory store that enforces `userId`. Cosmos emulator tests
(`Infrastructure.Tests/Storage/CosmosEmulatorTests.cs`) are skipped unless the emulator answers at
`COSMOS_EMULATOR_ENDPOINT` (default `https://localhost:8081/`).
