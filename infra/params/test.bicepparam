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

// Retention (D7): user-changeable default, 1..3650 days.
param chatRetentionDays = 30
// Sign-in buttons (D6, auth-setup section 7). Nonprod adds email one-time passcode.
param authProviders = '[{"id":"google","label":"Continue with Google","domainHint":"google"},{"id":"facebook","label":"Continue with Facebook","domainHint":"facebook"},{"id":"email","label":"Sign in with email"}]'
// Support contact for the "Delete my data" notice (optional in nonprod; empty = APP_SUPPORT_CONTACT not set).
param supportContact = ''
// Chat model deployment names in the Foundry project (D8, user input). REPLACE_ME is a placeholder: check-params.sh blocks
// deploys that still contain it, and the backend rejects <...>/REPLACE_ME deployment names at startup. Orchestrator on a
// cheaper model than the specialists.
param orchestratorDeployment = 'REPLACE_ME'
param specialistDeployment = 'REPLACE_ME'
