---
name: github-actions-pipelines
description: Conventions, variable contract and templates for this repo's GitHub Actions pipelines (CI, build/push, deploy to Azure Container Apps, infra). Use whenever creating or modifying workflows.
---
Templates (in this skill's /templates folder): ci.yml, deploy.yml, _deploy-env.yml, infra.yml.
Copy them to .github/workflows/ and adapt; do not rewrite from scratch.

Flow: ci (backend build+test, frontend lint+typecheck+test+build, docker build validation)
 -> build-push (once, tag = short SHA, shared ACR) -> deploy dev -> deploy test (approval) -> deploy prod (approval).
Each deploy: update backend app, then frontend app, with a unique revision suffix, then smoke test
GET https://<frontend fqdn>/api/healthz (backend ingress is internal; the nginx frontend proxies /api).

Assumptions to verify against the repo before finishing:
- Backend: `dotnet restore/build/test src/backend` works (single solution or project there), Dockerfile at src/backend/Dockerfile.
- Frontend: package.json has scripts `lint`, `typecheck`, `test` (non-watch, e.g. vitest run), `build`; Dockerfile at src/frontend/Dockerfile.
- Infra: infra/main.bicep and infra/params/{dev,test,prod}.bicepparam.
- Image names: agentic-workflow-backend, agentic-workflow-frontend.

GitHub Environment variables (not secrets), one set per environment: AZURE_CLIENT_ID, AZURE_TENANT_ID,
AZURE_SUBSCRIPTION_ID, AZURE_RESOURCE_GROUP, ACR_NAME, BACKEND_APP, FRONTEND_APP.
The dev identity needs AcrPush on the shared registry (build-push runs in the dev environment).
Container Apps pull images with their own managed identity (AcrPull), configured in Bicep, not in the pipeline.

Rollback = re-run the deploy workflow on the previous good commit (same image tag already in ACR).
Revision suffix must start with a letter and stay short (revision name length is limited).
Never put keys, connection strings or tokens into workflow files, logs or variables.
End-to-end UI tests with real social login are NOT run in the pipeline; use mocked auth if e2e is added to CI.