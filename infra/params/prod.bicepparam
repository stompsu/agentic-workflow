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

// Retention (D7): user-changeable default, 1..3650 days.
param chatRetentionDays = 90
// Sign-in buttons (D6, auth-setup section 7). Prod: social only, no email entry.
param authProviders = '[{"id":"google","label":"Continue with Google","domainHint":"google"},{"id":"facebook","label":"Continue with Facebook","domainHint":"facebook"}]'
// Support contact for the "Delete my data" notice (user input; REPLACE_ME is caught by check-params.sh).
param supportContact = 'REPLACE_ME'
// Chat model deployment names in the Foundry project (D8, user input). REPLACE_ME is a placeholder: check-params.sh blocks
// deploys that still contain it, and the backend rejects <...>/REPLACE_ME deployment names at startup. Orchestrator on a
// cheaper model than the specialists.
param orchestratorDeployment = 'REPLACE_ME'
param specialistDeployment = 'REPLACE_ME'

// Guest access (ADR 0006): OFF in prod. Enabling needs the ADR 0006 prod preconditions (CAPTCHA, G7) and a set alertEmail
// (check-params.sh fails if guestAccessEnabled = true and alertEmail is empty). It also adds Key Vault Crypto User for the
// app identity (prod has no Key Vault role today) and creates the guest-token-signing key.
param guestAccessEnabled = false
// Required together with guestAccessEnabled = true in prod (G7 CAPTCHA + prod Crypto User role in place); main.bicep fails and check-params.sh rejects otherwise.
// param guestProdPreconditionsAck = true
param guestMaxLifetimeHours = 4
param guestDataTtlMinutes = 255 // 4 h 15 min; >= lifetime + 5 min
// Optional caps (omit = backend defaults 200 / 30 / 5 / 300 / 2000):
// param guestMaxCreationsPerHour = 200
// param guestMaxTurns = 30
// param guestMaxSessions = 5
// param guestMaxTurnsPerHour = 300
// param guestMaxTurnsPerDay = 2000
// Operator email for the guest signing key alert; only needed when guestAccessEnabled = true.
param alertEmail = ''
