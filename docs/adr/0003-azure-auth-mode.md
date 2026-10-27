# ADR 0003: Azure auth mode (keys vs Managed Identity) and credentials

Status: Accepted (revised 2026-10-25). Decision D5 (managed identity for Foundry in all deployed environments, no
service-principal secret) is Decided; `.claude/agents/azure-infra.md` is aligned. Skill: azure-auth-switch.
2026-10-25: rules 9-10 added (D7 retention / delete-my-data config); D8 resolved (Foundry inputs supplied by the user).
2026-10-27: rules 12-17 added (ADR 0006 guest access, accepted); rule 11 is left to the local-mode ADR 0005 (other
branch). Guest token signing does not use `Azure:AuthMode`; rules 1-10 are unchanged.

## Context

Hard rule: Cosmos DB and AI Search use access keys in Development/Test and Managed Identity in Production, selected by
`Azure:AuthMode` (`Key` | `ManagedIdentity`); startup must fail if `AuthMode=Key` in Production. `AIProjectClient`
always uses a TokenCredential. Learn warns that `DefaultAzureCredential` in production can cause latency, unintended
credential probing and unpredictable behavior from environment variables, and recommends a deterministic credential.

## Decision

### 1. Environments

| Where | `ASPNETCORE_ENVIRONMENT` | `Azure:AuthMode` | Cosmos/Search auth | Foundry auth |
|---|---|---|---|---|
| Local | `Development` | `Key` (user-secrets) | key | developer login (`az login` / VS) |
| ACA dev | `Dev` | `Key` (Key Vault reference) | key | user-assigned MI |
| ACA test | `Test` | `Key` (Key Vault reference) | key | user-assigned MI |
| ACA test, MI smoke job | `Test` | `ManagedIdentity` | user-assigned MI | user-assigned MI |
| ACA prod | `Production` | `ManagedIdentity` | user-assigned MI | user-assigned MI |

Deployed dev/test do not use `Development` (avoids developer exception pages). Foundry uses the user-assigned MI
everywhere outside local development; there is no service-principal secret (D5).

### 1a. Pre-prod managed-identity smoke check

CLAUDE.md keeps keys in Test, so the MI path for Cosmos/Search would otherwise first run in prod. To catch RBAC and
config errors before the prod approval:
- azure-infra grants the test UAMI the prod-equivalent data roles on the test Cosmos account and Search service
  (Cosmos SQL "Built-in Data Contributor", "Search Index Data Reader") and sets the test Search service to accept
  both keys and Entra (`aadOrApiKey`); test keys stay enabled.
- A Container Apps Job in test runs the SAME image tag with `Azure__AuthMode=ManagedIdentity`, no `Cosmos__Key` /
  `Search__Key`, and the command `--smoke-check`: builds clients through `IAzureClientFactory`, does a Cosmos point
  read of a non-existent id (expects 404, fails on 401/403), one Search query with `top=1`, and acquires a Foundry
  token when `Foundry:ProjectEndpoint` is set (skipped and logged otherwise). Exit code 0/1; no data written.
  The job gets the same non-secret app settings as the backend app (including `Chat__RetentionDays`, rule 9).
- devops-pipelines runs the job after the test deploy; prod deployment requires it to pass (before approval).

### 2. One factory, services never read keys

```csharp
// SupportChat.Infrastructure.Azure
public interface IAzureCredentialProvider
{
    TokenCredential Credential { get; }       // singleton, used for AIProjectClient and for MI-mode Cosmos/Search
}

public interface IAzureClientFactory
{
    CosmosClient CreateCosmosClient();
    SearchClient CreateSearchClient(string indexName);
}
```
- Key mode: `new CosmosClient(Cosmos:Endpoint, Cosmos:Key, options)`, `new SearchClient(endpoint, index, new AzureKeyCredential(Search:Key))`.
- `Search:Key` for the API is a QUERY key (read-only). The admin key is never configured for the API; only ingestion
  / index management uses it (`Ingestion:SearchAdminKey`, data-model 2.6).
- ManagedIdentity mode: same constructors with `IAzureCredentialProvider.Credential`.
- `CosmosClient` and `SearchClient` are registered as singletons created by the factory; `CosmosClientOptions` use
  `System.Text.Json` serializer with camelCase properties and camelCase string enums, `ApplicationName = "supportchat-api"`.
- `ChatClientFactory` (Infrastructure implementation of the Core `IChatClientFactory`, ADR 0002 section 4) gets the
  credential from `IAzureCredentialProvider` and builds `AIProjectClient` lazily; there is no key path for models.
- Only the factory and options classes reference `Cosmos:Key` / `Search:Key`. qa-reviewer greps for any other use.

### 3. Credential construction (always a TokenCredential)

- `Development` (local): `new DefaultAzureCredential(new DefaultAzureCredentialOptions { ExcludeManagedIdentityCredential = true, ExcludeWorkloadIdentityCredential = true })`
  (faster local start, uses `az login` / Visual Studio).
- `Dev`, `Test`, `Production`: `DefaultAzureCredential` reduced to managed identity only:
  `ManagedIdentityClientId = AZURE_CLIENT_ID` and `ExcludeEnvironmentCredential`, `ExcludeWorkloadIdentityCredential`,
  `ExcludeVisualStudioCredential`, `ExcludeVisualStudioCodeCredential`, `ExcludeAzureCliCredential`,
  `ExcludeAzurePowerShellCredential`, `ExcludeAzureDeveloperCliCredential`, `ExcludeInteractiveBrowserCredential`,
  `ExcludeBrokerCredential` = true.
  Defense in depth: the Container App also sets `AZURE_TOKEN_CREDENTIALS=ManagedIdentityCredential`
  (supported for individual credential names since Azure.Identity 1.15.0; we use 1.21.0).
  This satisfies "DefaultAzureCredential" from CLAUDE.md while behaving like a deterministic `ManagedIdentityCredential`.
  (Equivalent alternative if the team prefers: `new ManagedIdentityCredential(ManagedIdentityId.FromUserAssignedClientId(id))`
  behind the same provider; still a TokenCredential.)

### 4. Fail-fast startup validation (`IValidateOptions` + `ValidateOnStart`)

Environment names and `AuthMode` values are compared case-insensitively (`StringComparison.OrdinalIgnoreCase`;
`IHostEnvironment.IsEnvironment` already is). Startup throws (app does not start, readiness never passes) when:
1. `Azure:AuthMode` missing or not `Key`/`ManagedIdentity`.
2. Environment is `Production` and `AuthMode != ManagedIdentity`.
3. `AuthMode = Key` and environment is not in the allow-list `Development`, `Dev`, `Test` (catches typos such as `Prod`).
4. `AuthMode = Key` and `Cosmos:Key` or `Search:Key` empty.
5. Environment is `Production` and `Cosmos:Key` or `Search:Key` is present at all (keys must not even be configured).
6. Environment is not `Development` and `AZURE_CLIENT_ID` is empty.
7. Any of `Cosmos:Endpoint`, `Search:Endpoint`, `AzureAd:*` missing; and `Foundry:ProjectEndpoint` missing while the
   registry contains any workflow other than `echo` (the check runs after registration, against `IWorkflowRegistry`).
8. `Workflows:Echo:Enabled = true` and environment is not in the allow-list `Development`, `Dev`, `Test`
   (so it always fails in `Production`).
9. `Chat:RetentionDays` is set but not an integer in 1..3650, OR it is unset and the environment is not `Development`
   (D7: every deployed environment must expire data; defaults dev 30, test 30, prod 90, data-model 1.3).
10. `Chat:DeleteUserDataBudget` is not a `TimeSpan` in `00:00:05`..`00:03:00` (default `00:01:00`; must stay below the
    240 s ingress timeout, api-contract 2.8).
Rules 9-10 run in the API host only (web app and `--smoke-check`), not in the ingestion console app.

Guest access (ADR 0006 section 11, accepted 2026-10-27). Rules 12-17 apply only when `Guest:Enabled = true`; when it
is false the other `Guest:*` keys are ignored, except `Guest:DataTtl` (default `04:15:00`): the retention policy still
uses it for leftover guest data (data-model 1.3), so its range `00:35:00`..`7.00:00:00` is checked in every case. Like rules 9-10 they run in the API host only (web app and `--smoke-check`).
12. `Guest:Issuer` missing, not matching `^[a-z0-9-]{3,64}$` (so it can never be an Entra issuer, which is always an
    https URL), or equal to any issuer the Entra scheme accepts (derived from `AzureAd:Instance` / `AzureAd:TenantId`,
    or configured explicitly).
13. `Guest:Audience` not matching `^[a-z0-9-]{3,64}$`, or the audiences overlap in either direction: `Guest:Audience`
    equals `AzureAd:ClientId`, `api://<ClientId>` or any configured `AzureAd:Audience(s)`, or any valid audience of the
    Entra scheme equals `Guest:Audience`.
14. `Guest:SigningMode` not `KeyVault`/`Ephemeral`, or `Ephemeral` and the environment is not `Development` (so it
    always fails in `Production`).
15. `Guest:SigningMode = KeyVault` and `Guest:SigningKeyUri` missing, not `https`, host not ending in
    `.vault.azure.net`, or path not exactly `/keys/<name>` (versionless; a version segment fails startup).
16. A value outside its range: `Guest:SigningKeyRefreshInterval` `00:05:00`..`01:00:00`; `Guest:MaxLifetime`
    `00:30:00`..`12:00:00`; `Guest:DataTtl` `>= MaxLifetime + 00:05:00` and `<= 7.00:00:00`;
    `Guest:ActiveCheckCacheDuration` `00:00:10`..`00:05:00`; `Guest:MaxTurns` 1..1000; `Guest:MaxSessions` 1..100;
    `Guest:MaxTurnsPerDay <= 24 x Guest:MaxTurnsPerHour`; the other limits in ADR 0006 section 11 (`TurnsPerMinute`,
    `CreationsPerClientPerHour`, `MaxCreationsPerHour`, `MaxTurnsPerHour`, `MaxTurnsPerDay`) are integers >= 1.
17. None of the `Guest:Workflows` ids is registered (the check runs after registration, against `IWorkflowRegistry`,
    like rule 7).

Numbering reconciliation: the local-mode branch (ADR 0005, unmerged) adds a rule 11. This branch therefore starts
its rules at 12 and leaves 11 unused, so neither branch renumbers when both merge. If the local-mode branch is
abandoned, rule 11 stays an intentional gap (do not renumber 12-17; tests and docs reference them). Whichever branch
merges second checks that its rule texts did not change meaning in the meantime.

Foundry inputs (D8, decided): the user supplies the Foundry project resource id (`foundryProjectResourceId`), the
project endpoint (`foundryProjectEndpoint` -> `Foundry:ProjectEndpoint`) and the model resource id
(`modelResourceId`, the account hosting the embedding deployment) per environment; azure-infra references them and
creates only role assignments. Until the user has supplied them for an environment, that environment runs as before
(Phase 1 placeholder approach): only the echo workflow registered (dev), `Foundry:ProjectEndpoint` unset, no
"Azure AI User" role assignment. `ChatClientFactory` is registered but never builds `AIProjectClient` because nothing
calls `Create`. Once the endpoint and the Bicep param are set together and SupportTicket is registered, rule 7
enforces the endpoint.

### 5. Secrets
- Local: `dotnet user-secrets` for `Cosmos:Key`, `Search:Key` (query key).
- Dev/Test ACA: Container App secrets with `keyVaultUrl` references (read by the app's user-assigned MI, which holds
  "Key Vault Secrets User" on the vault, Phase 1), mapped to env vars `Cosmos__Key`, `Search__Key`. Never in Bicep
  params, pipeline variables or logs.
  - Cosmos key: written to Key Vault by Bicep (`listKeys()` output straight into a `Microsoft.KeyVault/vaults/secrets`
    resource, never a Bicep output).
  - Search QUERY key: Bicep cannot create query keys. `infra/scripts/ensure-search-query-key.sh` (owner azure-infra;
    run by the user once per dev/test environment after the first infra deploy, before the backend revision needs the
    secret; documented in `infra/README.md`) creates query key `supportchat-api` if absent and writes it to Key Vault
    secret `search-query-key` without printing it. Not run by pipelines in v1.
- No Foundry secret in any environment (D5).
- Prod: no keys anywhere; Cosmos `disableLocalAuth = true`, Search `disableLocalAuth = true` (azure-infra); no query key.

### 6. RBAC for the app identity (azure-infra)
Dev and test (Phase 1): "Key Vault Secrets User" on the environment's Key Vault.
Prod and test (test: for the smoke job, 1a): Cosmos SQL role "Cosmos DB Built-in Data Contributor" scoped to the
database; "Search Index Data Reader" on the Search service. All deployed envs: "Azure AI User" on the Foundry project,
created only when the optional Bicep param `foundryProjectResourceId` is set; "AcrPull" on ACR. Ingestion identity
separately gets "Search Index Data Contributor" (+ "Search Service Contributor" for index management, + Foundry
access for embeddings). "Delete my data" needs no extra role: Data Contributor already allows deletes.

## Tests (backend-dotnet)
- Each factory path builds clients without network calls (Key mode, MI mode with a stub `TokenCredential`).
- Validation matrix: each rule in 4 has a failing test; `Production + Key` throws; `production` (lowercase) behaves
  like `Production`; echo enabled in `Production` throws; echo-only registry without `Foundry:ProjectEndpoint` starts;
  any non-echo workflow without it throws; `Chat:RetentionDays` unset starts in `Development` and throws in `Dev`,
  `Test`, `Production`; `0`, `3651` and a non-integer throw; budget `00:00:00` and `00:05:00` throw.
- Guest rules 12-17: `Guest:Enabled = false` with invalid `Guest:*` values starts, except an out-of-range
  `Guest:DataTtl`, which throws; an https-URL issuer, an Entra
  issuer, `Guest:Audience` equal to `AzureAd:ClientId` / `api://<ClientId>` throw; `Ephemeral` throws in `Dev`,
  `Test` and `Production` and starts in `Development`; a versioned or non-vault `SigningKeyUri` throws;
  `DataTtl = MaxLifetime + 00:04:59` throws and `+ 00:05:00` starts; `MaxTurnsPerDay > 24 x MaxTurnsPerHour` throws;
  `Guest:Workflows` with no registered id throws.
- `ChatClientFactory` constructs `AIProjectClient` only with a `TokenCredential` (type-level: no overload with a key
  exists) and not before the first `Create`.
- `--smoke-check` returns non-zero when Cosmos or Search reject the credential (stubbed responses). With guests enabled
  in `KeyVault` mode it also loads the signing key versions, fails when no version is eligible for signing or Key Vault
  returns 403 (message names the "Key Vault Crypto User" role), warns when the signing version expires within 30 days,
  and signs and verifies a fixed test payload (ADR 0006 section 11).
