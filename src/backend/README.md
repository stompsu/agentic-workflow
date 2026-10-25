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
# SupportTicket is always registered, so these are required whenever the app runs (startup validation rule 7):
dotnet user-secrets set "Foundry:ProjectEndpoint" "https://<res>.services.ai.azure.com/api/projects/<project>"
dotnet user-secrets set "Workflows:SupportTicket:Agents:Orchestrator:Deployment"       "<chat deployment name>"
dotnet user-secrets set "Workflows:SupportTicket:Agents:Policy:Deployment"             "<chat deployment name>"
dotnet user-secrets set "Workflows:SupportTicket:Agents:Claims:Deployment"             "<chat deployment name>"
dotnet user-secrets set "Workflows:SupportTicket:Agents:HealthAssessments:Deployment"  "<chat deployment name>"
# optional: Workflows:SupportTicket:ConfidenceThreshold (0.7), :MaxConsecutiveClarifications (2),
#           :Agents:<Key>:Temperature
```

Optional chat limits live under `Chat:*` (plan.md section 5). Ingestion keys: see `src/SupportChat.Ingestion`.

Foundry is always accessed with a `TokenCredential` (`az login` locally); there is no model key.
Deployed environments (`Dev`, `Test`, `Production`) set the same keys as environment variables with `__`
(for example `Cosmos__Key` from a Key Vault reference). `Production` must use `Azure__AuthMode=ManagedIdentity`
with `AZURE_CLIENT_ID` and no `Cosmos__Key` / `Search__Key` at all; the app refuses to start otherwise
(ADR 0003 section 4).

## Tests

`dotnet test src/backend` runs everything offline: Api.Tests uses the real JWT validation with a test signing key,
the real endpoints and an in-memory store that enforces `userId`. Cosmos emulator tests
(`Infrastructure.Tests/Storage/CosmosEmulatorTests.cs`) are skipped unless the emulator answers at
`COSMOS_EMULATOR_ENDPOINT` (default `https://localhost:8081/`).
