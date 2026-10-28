# Agentic Workflow

A chat application where users sign in with a social account (Google, Facebook, ...) and talk to a multi-agent
workflow. The first workflow, **SupportTicket**, classifies a request as Policy, Claims or HealthAssessments and routes it
to a specialist agent that answers with citations from an Azure AI Search knowledge index. New workflow types can be added
without touching the chat endpoints or the chat UI.

## Architecture

```mermaid
flowchart LR
  U[User browser] -->|OIDC login| E[Microsoft Entra External ID<br/>Google / Facebook]
  U -->|HTTPS, SSE| FE[Frontend container<br/>nginx + React SPA]
  FE -->|/api reverse proxy<br/>same origin| BE[Backend container<br/>.NET 10 Web API + MAF workflows]
  BE -->|validate JWT, oid claim| E
  BE --> CO[(Cosmos DB<br/>sessions, history)]
  BE --> AIS[(Azure AI Search<br/>knowledge)]
  BE -->|AIProjectClient + TokenCredential| FD[Azure AI Foundry<br/>models]
  ING[Ingestion job] --> AIS
  subgraph ACA[Azure Container Apps environment per stage: dev, test, prod]
    FE
    BE
    ING
  end
  ACR[(Shared ACR)] -.->|same image tag promoted| ACA
```

- Agents are defined in code (`ChatClientAgent`), not as server-side Foundry agents.
- Cosmos and Search: access keys in Dev/Test, managed identity in Production (`Azure:AuthMode`); startup fails if
  `Key` is used in Production. Models always use a `TokenCredential`.
- User identity is the `oid` claim of the validated token; every data access is scoped to it.

## Repository layout

```
src/backend/      .NET 10 solution (AgenticWorkflow.slnx): Core, Infrastructure, Agents, Api, Ingestion + tests; Dockerfile(s)
src/frontend/     Vite + React + TypeScript SPA, nginx config, Dockerfile, Playwright e2e
infra/            Bicep (main.bicep, modules/, params/{dev,test,prod}.bicepparam, scripts/)
knowledge/        source documents for ingestion (policy, claims, health)
docs/             plan, API contract, data model, ADRs, auth and CI/CD setup
scripts/          setup-github-oidc.sh (one-time, run by the user)
.github/          workflows, dependabot
.claude/          agent and skill definitions for this repo
```

## Local development

Backend (details: [src/backend/README.md](src/backend/README.md)):
```bash
cd src/backend/src/AgenticWorkflow.Api
dotnet user-secrets set "Cosmos:Endpoint" "https://<account>.documents.azure.com:443/"
dotnet user-secrets set "Cosmos:Key" "<key>"          # Development uses Azure:AuthMode=Key
dotnet user-secrets set "Search:Endpoint" "..."; dotnet user-secrets set "Search:Key" "<query key>"
# plus AzureAd:* and Foundry:ProjectEndpoint, see the backend README
cd ../../../.. && dotnet run --project src/backend/src/AgenticWorkflow.Api   # http://localhost:5080
```
Secrets are never committed; use user-secrets or Key Vault references.

Frontend ([src/frontend/README.md](src/frontend/README.md)), Node version from `src/frontend/.nvmrc`:
```bash
cd src/frontend && npm ci && npm run dev    # http://localhost:5173, /api proxied to http://localhost:5080
```
Override the proxy target with `BACKEND_URL`. Runtime config comes from `/config.json` (`public/config.json` locally).

Cosmos emulator: `CosmosEmulatorTests` in `AgenticWorkflow.Infrastructure.Tests` run against the Azure Cosmos DB emulator and are
skipped unless it answers at `COSMOS_EMULATOR_ENDPOINT` (default `https://localhost:8081/`; key override
`COSMOS_EMULATOR_KEY`). Start the emulator, then `dotnet test src/backend`. CI has no emulator, so they are skipped there.

## Tests

```bash
dotnet test src/backend                       # unit + API tests (fake auth, fake chat client, in-memory stores)
cd src/frontend && npm run lint && npm run typecheck && npm test
cd src/frontend && npx playwright install chromium && npm run e2e   # mocked auth + API; no real social login
```
Real social login is verified manually (not in the pipeline).

## Deploy flow

```
PR            -> ci (build/test, lint, e2e, bicep lint, docker build validation)
push to main  -> ci -> build + push agentic-workflow-{backend,frontend,ingestion}:<short-sha> to the shared ACR (once)
              -> deploy dev -> deploy test (approval) -> MI smoke job (--smoke-check) -> deploy prod (approval)
```
Same image tag is promoted through all stages. Azure authentication is OIDC (no stored Azure secrets). Infra changes go
through the manually dispatched `infra.yml` or are deployed from a laptop (no automatic infra runs; PRs get a Bicep lint). Setup and rollback: [docs/cicd-setup.md](docs/cicd-setup.md),
[infra/README.md](infra/README.md).

## Documentation

| Doc | Content |
|---|---|
| [docs/plan.md](docs/plan.md) | delivery plan, config keys, decisions |
| [docs/api-contract.md](docs/api-contract.md) | HTTP + SSE contract (change it first) |
| [docs/data-model.md](docs/data-model.md) | Cosmos data model |
| [docs/auth-setup.md](docs/auth-setup.md) | Entra External ID, Google, Facebook (manual) |
| [docs/cicd-setup.md](docs/cicd-setup.md) | GitHub Environments, OIDC, variables |
| [docs/adr/](docs/adr) | identity flow, workflow abstraction, Azure auth mode |
| [infra/README.md](infra/README.md) | Bicep modules, deploy order, pipeline roles |

## Adding a workflow type

Follow the checklist in [.claude/skills/add-workflow-type/SKILL.md](.claude/skills/add-workflow-type/SKILL.md): implement
the workflow definition and register it, add knowledge domains and ingestion, add routing tests with a fake chat client,
and confirm the UI dropdown shows it with zero frontend changes.
