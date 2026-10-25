# Support Ticket Agentic System

## Architecture
- Frontend: React + TypeScript (Vite), chat UI with streaming, login, history, workflow dropdown.
- Backend: .NET 10 Web API hosting Microsoft Agent Framework (MAF) workflows.
- Knowledge: Azure AI Search. Session/history store: Cosmos DB.
- Workflow "SupportTicket": orchestrator agent classifies into Policy | Claims | HealthAssessments,
  then routes to the matching specialist agent.
- New workflow types must be addable WITHOUT changing the chat endpoints or the chat UI.

## Hard rules
- Auth to Azure services: access keys in Development/Test, Managed Identity (DefaultAzureCredential) in Production.
  Selected ONLY via configuration (`Azure:AuthMode` = Key | ManagedIdentity). Never hardcode secrets.
- MAF's API changes quickly. Before writing MAF code, check current Microsoft Learn docs
  and the installed package versions. Do not rely on memory.
- Streaming uses Server-Sent Events. Contract lives in /docs/api-contract.md. Change it there first.
- Every feature needs tests. Run `dotnet test` and `npm test` before declaring done.

## Conventions
- Backend: C#, nullable enabled, minimal APIs, options pattern, DI everywhere, no static state.
- Frontend: strict TypeScript, functional components, React Query for server state.
- Commits small and scoped. ADRs in /docs/adr for any non-obvious decision.