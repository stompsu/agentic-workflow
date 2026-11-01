using '../main.bicep'

// dev owns the shared ACR. Non-secret placeholders only; replace REPLACE_ME values (see infra/README.md).
param environmentName = 'dev'
param acrName = 'acragentwfshared' // must be globally unique: change before the first deploy
param createAcr = true
// westeurope had no Search Basic capacity on 2026-10-29 (ResourcesForSkuUnavailable).
param searchLocation = 'northeurope'
// Bound once with az containerapp hostname bind (infra/README.md). Removing these removes the binding on the next infra deploy.
param frontendCustomDomain = 'agenticworkflow.highpace.dev'
param frontendCustomDomainCertificateName = 'mc-cae-agentwf-de-agenticworkflow--7648'

param llmContentTelemetryEnabled = true // records prompts and user text in Application Insights (plan.md D14)
param echoWorkflowEnabled = true // Phase 1 walking skeleton (ADR 0003: allowed in Dev)
param entraInstance = 'https://agentwfdev.ciamlogin.com/'
param entraTenantId = '625c8458-1b7d-48c2-886d-1818a1dffd98'
param apiClientId = 'f520805d-5e08-485a-9463-3d22f9011454'
param spaClientId = '894d720f-6efd-4b14-8079-54ca491d2423'
param spaAuthority = 'https://agentwfdev.ciamlogin.com/agentwfdev.onmicrosoft.com'
param apiScope = 'api://f520805d-5e08-485a-9463-3d22f9011454/access_as_user'

// Foundry project (D8) lives in rg-agentwf-ai; the app identity gets "Azure AI User" on it.
param foundryProjectResourceId = '/subscriptions/47a28b0b-6638-4e6a-a788-c283b705b008/resourceGroups/rg-agentwf-ai/providers/Microsoft.CognitiveServices/accounts/agentwf-resource/projects/agentwf-dev'
param foundryProjectEndpoint = 'https://agentwf-resource.services.ai.azure.com/api/projects/agentwf-dev'
param writeSearchQueryKeyFromBicep = true

// Ingestion job (manual trigger). Needs foundryProjectEndpoint, modelResourceId (or vectorizerResourceUri) and an
// image built from Dockerfile.ingestion (knowledge/ at /app/knowledge). Pass ingestionImage from the pipeline.
param deployIngestionJob = false
param embeddingDeployment = 'embedding' // deployment name in agentwf-resource (model text-embedding-3-small)
param modelResourceId = '/subscriptions/47a28b0b-6638-4e6a-a788-c283b705b008/resourceGroups/rg-agentwf-ai/providers/Microsoft.CognitiveServices/accounts/agentwf-resource' // Foundry/Azure OpenAI account that hosts the embedding deployment

// Retention (D7): user-changeable default, 1..3650 days.
param chatRetentionDays = 30
// Sign-in buttons (D6, auth-setup section 7). Nonprod adds email one-time passcode.
param authProviders = '[{"id":"google","label":"Continue with Google","domainHint":"google"},{"id":"facebook","label":"Continue with Facebook","domainHint":"facebook"},{"id":"email","label":"Sign in with email"}]'
// Support contact for the "Delete my data" notice (optional in nonprod; empty = APP_SUPPORT_CONTACT not set).
param supportContact = ''
// Chat model deployment names in the Foundry project (D8, user input). REPLACE_ME is a placeholder: check-params.sh blocks
// deploys that still contain it, and the backend rejects <...>/REPLACE_ME deployment names at startup. Orchestrator on a
// cheaper model than the specialists.
param orchestratorDeployment = 'orchestrator'
param specialistDeployment = 'specialist'

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
