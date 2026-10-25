using '../main.bicep'

// test references the shared ACR created by the dev deployment (same name; RG = dev resource group).
param environmentName = 'test'
param acrName = 'acrsupportchatshared'
param createAcr = false
param acrResourceGroup = 'rg-supportchat-dev' // RG that holds the shared ACR; adjust to your naming

param echoWorkflowEnabled = false
param entraInstance = 'https://REPLACE_ME.ciamlogin.com/'
param entraTenantId = '00000000-0000-0000-0000-000000000000'
param apiClientId = '00000000-0000-0000-0000-000000000000'
param spaClientId = '00000000-0000-0000-0000-000000000000'
param spaAuthority = 'https://REPLACE_ME.ciamlogin.com/REPLACE_ME.onmicrosoft.com'
param apiScope = 'api://REPLACE_ME/access_as_user'

param foundryProjectResourceId = ''
param foundryProjectEndpoint = ''
param writeSearchQueryKeyFromBicep = true
// Enable once a backend image with --smoke-check exists and backendImage is passed (ADR 0003 section 1a).
param deploySmokeJob = false

// Ingestion job (manual trigger). Needs foundryProjectEndpoint, modelResourceId (or vectorizerResourceUri) and an
// image built from Dockerfile.ingestion (knowledge/ at /app/knowledge). Pass ingestionImage from the pipeline.
param deployIngestionJob = false
param embeddingDeployment = 'text-embedding-3-small'
param modelResourceId = '' // Foundry/Azure OpenAI account that hosts the embedding deployment
