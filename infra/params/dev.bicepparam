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

// Guest access (ADR 0006): on in nonprod. Creates Key Vault key guest-token-signing and Key Vault Crypto User for the app identity.
param guestAccessEnabled = true
param guestMaxLifetimeHours = 4
param guestDataTtlMinutes = 255 // 4 h 15 min; >= lifetime + 5 min
// Optional caps (omit = backend defaults 200 / 30 / 5 / 300 / 2000):
// param guestMaxCreationsPerHour = 200
// param guestMaxTurns = 30
// param guestMaxSessions = 5
// param guestMaxTurnsPerHour = 300
// param guestMaxTurnsPerDay = 2000
// Operator email for the guest signing key alert (user input, not a secret, do not commit a real address): REPLACE_ME is a
// placeholder caught by check-params.sh: set it locally for the deploy and do not commit a real address.
param alertEmail = 'REPLACE_ME'
