using '../main.bicep'

// prod: managed identity only. No Cosmos/Search keys, no Key Vault secrets, local auth disabled.
param environmentName = 'prod'
param acrName = 'acrsupportchatshared'
param createAcr = false
param acrResourceGroup = 'rg-supportchat-dev' // RG that holds the shared ACR; adjust to your naming

param echoWorkflowEnabled = false // startup fails if true in Production
param entraInstance = 'https://REPLACE_ME.ciamlogin.com/'
param entraTenantId = '00000000-0000-0000-0000-000000000000'
param apiClientId = '00000000-0000-0000-0000-000000000000'
param spaClientId = '00000000-0000-0000-0000-000000000000'
param spaAuthority = 'https://REPLACE_ME.ciamlogin.com/REPLACE_ME.onmicrosoft.com'
param apiScope = 'api://REPLACE_ME/access_as_user'

param foundryProjectResourceId = ''
param foundryProjectEndpoint = ''
param cosmosAutoscaleMaxThroughput = 4000

// Ingestion job (manual trigger). Needs foundryProjectEndpoint, modelResourceId (or vectorizerResourceUri) and an
// image built from Dockerfile.ingestion (knowledge/ at /app/knowledge). Pass ingestionImage from the pipeline.
param deployIngestionJob = false
param embeddingDeployment = 'text-embedding-3-small'
param modelResourceId = '' // Foundry/Azure OpenAI account that hosts the embedding deployment
