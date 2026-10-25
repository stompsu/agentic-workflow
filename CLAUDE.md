## Architecture
- Frontend: React + TypeScript (Vite). Chat UI with streaming, login, history, workflow dropdown.
  Served by an nginx container; /api is reverse-proxied to the backend (same origin, no CORS).
- Backend: .NET 10 Web API hosting Microsoft Agent Framework (MAF) workflows.
- Models: via Azure.AI.Projects `AIProjectClient` -> IChatClient. Agents are defined in code (ChatClientAgent),
  NOT as server-side Foundry agents or Foundry workflows (Foundry workflows are being retired).
- Knowledge: Azure AI Search. Session/history: Cosmos DB.
- Hosting: Azure Container Apps (frontend app + backend app, one Container Apps environment per stage).
- Login: social providers (Google, Facebook, ...) through Microsoft Entra External ID. Backend validates JWTs only.
- Workflow "SupportTicket": orchestrator classifies into Policy | Claims | HealthAssessments, then routes to the specialist.
- New workflow types must be addable WITHOUT changing chat endpoints or the chat UI.

## Hard rules
- Cosmos + AI Search: access keys in Development/Test, Managed Identity in Production, chosen by config `Azure:AuthMode`
  (Key | ManagedIdentity). Startup must FAIL if AuthMode=Key in Production.
- AIProjectClient always uses a TokenCredential (DefaultAzureCredential). No model keys.
- Never hardcode secrets. Dev/Test secrets come from Key Vault references or user-secrets.
- MAF and Azure.AI.Projects APIs change quickly: check current Microsoft Learn docs and installed package versions
  before writing code.
- SSE over fetch + ReadableStream (needs Authorization header). Contract: /docs/api-contract.md. Change it there first.
- User identity = `oid` claim from the validated token. Every data access is scoped to it.
- Pipelines live in .github/ and are owned by devops-pipelines. OIDC only, build once and promote the same image tag, prod requires approval. Never run workflows or change GitHub/Azure settings without asking.