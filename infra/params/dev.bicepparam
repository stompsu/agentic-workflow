using '../main.bicep'

// dev owns the shared ACR. Non-secret placeholders only; replace REPLACE_ME values (see infra/README.md).
param environmentName = 'dev'
param acrName = 'acrsupportchatshared' // must be globally unique: change before the first deploy
param createAcr = true

param echoWorkflowEnabled = true // Phase 1 walking skeleton (ADR 0003: allowed in Dev)
param entraInstance = 'https://REPLACE_ME.ciamlogin.com/'
param entraTenantId = '00000000-0000-0000-0000-000000000000'
param apiClientId = '00000000-0000-0000-0000-000000000000'
param spaClientId = '00000000-0000-0000-0000-000000000000'
param spaAuthority = 'https://REPLACE_ME.ciamlogin.com/REPLACE_ME.onmicrosoft.com'
param apiScope = 'api://REPLACE_ME/access_as_user'

// Foundry project does not exist yet (D8): no role assignment, no endpoint.
param foundryProjectResourceId = ''
param foundryProjectEndpoint = ''
param writeSearchQueryKeyFromBicep = true

// Ingestion job (manual trigger). Needs foundryProjectEndpoint, modelResourceId (or vectorizerResourceUri) and an
// image built from Dockerfile.ingestion (knowledge/ at /app/knowledge). Pass ingestionImage from the pipeline.
param deployIngestionJob = false
param embeddingDeployment = 'text-embedding-3-small'
param modelResourceId = '' // Foundry/Azure OpenAI account that hosts the embedding deployment
