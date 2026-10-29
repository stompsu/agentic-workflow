// Agentic Workflow infrastructure for ONE environment (dev | test | prod), deployed at resource-group scope:
//   az deployment group create -g <rg> --parameters infra/params/<env>.bicepparam
// See infra/README.md. No secret values pass through parameters or outputs.
targetScope = 'resourceGroup'

@allowed([ 'dev', 'test', 'prod' ])
param environmentName string
param location string = resourceGroup().location
@minLength(3)
@maxLength(12)
param namePrefix string = 'agentwf'

// ---- shared registry -------------------------------------------------------------------------------------------
@description('Name of the shared ACR (5-50 alphanumeric, globally unique).')
param acrName string
@description('true only in the environment that owns the shared registry (dev). test/prod reference it.')
param createAcr bool = false
@description('Resource group and subscription of the shared ACR (defaults: this RG / subscription).')
param acrResourceGroup string = resourceGroup().name
param acrSubscriptionId string = subscription().subscriptionId
@description('Assign AcrPull to the app identity. Needs role-assignment rights on the ACR scope.')
param assignAcrPull bool = true

// ---- images (empty = public placeholder image, no probes; the pipeline then rolls real images) ----------------------
param backendImage string = ''
param frontendImage string = ''
param containerPort int = 8080

// ---- scale ---------------------------------------------------------------------------------------------------------
param backendMinReplicas int = environmentName == 'prod' ? 1 : 0
param backendMaxReplicas int = 5
param backendConcurrentRequests int = 20
param frontendMinReplicas int = environmentName == 'prod' ? 1 : 0
param frontendMaxReplicas int = 3
param frontendConcurrentRequests int = 50
param cosmosAutoscaleMaxThroughput int = 4000
param searchSku string = 'basic'
@description('Region for AI Search only (e.g. when the RG region has no capacity for the SKU). Default = location.')
param searchLocation string = location

// ---- app configuration (non-secret; docs/plan.md section 5) --------------------------------------------------------
// Required (no defaults). Placeholders are rejected by scripts/check-params.sh (Bicep assert is experimental).
param entraInstance string
param entraTenantId string
param apiClientId string
param requiredScopes string = 'access_as_user'
param spaClientId string
param spaAuthority string
param apiScope string
param searchIndexName string = 'agentwf-knowledge'
param echoWorkflowEnabled bool = false

// ---- retention, sign-in options, workflow models (plan.md section 3 Phase 5; D6, D7, D8) ---------------------------
@description('Chat retention in days (D7): backend Chat__RetentionDays, frontend APP_RETENTION_DAYS. User-changeable defaults: dev 30, test 30, prod 90.')
@minValue(1)
@maxValue(3650)
param chatRetentionDays int
@description('JSON array for the frontend AUTH_PROVIDERS (D6). Nonprod includes the email entry; prod must not. Empty = env var not set.')
param authProviders string = ''
@description('Support contact shown in the "Delete my data" notice (APP_SUPPORT_CONTACT, plain text, max 200 chars). Empty = not set.')
@maxLength(200)
param supportContact string = ''
@description('Chat model deployment for the SupportTicket orchestrator (D8, a cheaper model). Empty = env var not set.')
param orchestratorDeployment string = ''
@description('Chat model deployment for the Policy, Claims and HealthAssessments specialists (D8). Empty = env vars not set.')
param specialistDeployment string = ''

// ---- guest access (ADR 0006 sections 11-12; dev true, test true, prod false) ---------------------------------------
@description('Enable "Continue as guest": backend Guest__Enabled, frontend AUTH_GUEST_ENABLED, the Key Vault signing key guest-token-signing and the Crypto User role. Prod stays false until the ADR 0006 prod preconditions (CAPTCHA, role) are met.')
param guestAccessEnabled bool = false
@description('Prod only: explicit acknowledgement that the ADR 0006 prod preconditions are met (G7 CAPTCHA in place and the prod Key Vault Crypto User role approved). Required to enable guests in prod: main.bicep fails and check-params.sh rejects guestAccessEnabled=true without it. Ignored in dev/test.')
param guestProdPreconditionsAck bool = false
@description('Guest identity lifetime in hours: backend Guest__MaxLifetime, frontend APP_GUEST_SESSION_HOURS (1..12).')
@minValue(1)
@maxValue(12)
param guestMaxLifetimeHours int = 4
@description('Guest data TTL in minutes: backend Guest__DataTtl; must be >= guestMaxLifetimeHours*60 + 5 (255 = 4 h 15 min). Frontend APP_GUEST_RETENTION_HOURS = this rounded up to whole hours.')
@minValue(35)
@maxValue(10080)
param guestDataTtlMinutes int = 255
@description('Optional caps (null = backend default): Guest__MaxCreationsPerHour (200), Guest__MaxTurns (30), Guest__MaxSessions (5), Guest__MaxTurnsPerHour (300), Guest__MaxTurnsPerDay (2000).')
param guestMaxCreationsPerHour int?
param guestMaxTurns int?
param guestMaxSessions int?
param guestMaxTurnsPerHour int?
param guestMaxTurnsPerDay int?
@description('Operator email for the guest signing key alert (deploy-time value, not a secret, never committed). Required when guestAccessEnabled is true (check-params.sh enforces it).')
param alertEmail string = ''

// ---- Foundry / models (referenced, never created) ------------------------------------------------------------------
@description('Resource id of an existing Foundry project. Empty = no "Azure AI User" assignment.')
param foundryProjectResourceId string = ''
@description('Foundry project endpoint for Foundry__ProjectEndpoint. Empty = not set (echo-only phase).')
param foundryProjectEndpoint string = ''
@description('Resource id of the model/embedding account for the Search vectorizer role (Phase 2). Empty = skip.')
param modelResourceId string = ''

// ---- search query key ----------------------------------------------------------------------------------------------
@description('dev/test: write the default Search query key to Key Vault from Bicep (listQueryKeys). Set false to use scripts/ensure-search-query-key.sh.')
param writeSearchQueryKeyFromBicep bool = true

// ---- test smoke job ------------------------------------------------------------------------------------------------
@description('test only: create the manual MI smoke job (needs a real backend image).')
param deploySmokeJob bool = false

// ---- ingestion job -------------------------------------------------------------------------------------------------
@description('Image of the ingestion console app (shared ACR: agentic-workflow-ingestion). Empty = no job.')
param ingestionImage string = ''
@description('Create the dedicated ingestion identity and the manual ingestion Container Apps Job (needs ingestionImage).')
param deployIngestionJob bool = false
@description('Embedding model deployment name in the Foundry project (Ingestion__EmbeddingDeployment, Ingestion__VectorizerDeployment).')
param embeddingDeployment string = 'text-embedding-3-small'
@description('Resource uri of the model account for the integrated vectorizer. Empty = derived from modelResourceId (https://<account>.openai.azure.com), or unset.')
param vectorizerResourceUri string = ''
@description('Source folder inside the ingestion image (Dockerfile.ingestion copies knowledge/ to /app/knowledge).')
param ingestionSourcePath string = '/app/knowledge'

// ---------------------------------------------------------------------------------------------------------------------
var isProd = environmentName == 'prod'
var isTest = environmentName == 'test'
var useKeys = !isProd                 // Key mode: dev and test (Azure__AuthMode=Key, KV secret references)
var grantDataRoles = isProd || isTest // data-plane roles for the app identity (test: for the MI smoke job)
var aspNetEnvironment = environmentName == 'dev' ? 'Dev' : (isTest ? 'Test' : 'Production')
var ingestionEnabled = deployIngestionJob && !empty(ingestionImage)
// Prod guard: guests in prod need the ADR 0006 preconditions acknowledged (fail() aborts template evaluation).
var guestOn = (isProd && guestAccessEnabled && !guestProdPreconditionsAck) ? fail('guestAccessEnabled=true in prod requires guestProdPreconditionsAck=true (G7 CAPTCHA + prod Key Vault Crypto User role, ADR 0006).') : guestAccessEnabled
var guestKeyName = 'guest-token-signing'
var guestLifetime = '${padLeft(string(guestMaxLifetimeHours), 2, '0')}:00:00'
var guestTtlDays = guestDataTtlMinutes / 1440
var guestTtlTime = '${padLeft(string((guestDataTtlMinutes % 1440) / 60), 2, '0')}:${padLeft(string(guestDataTtlMinutes % 60), 2, '0')}:00'
var guestDataTtl = guestTtlDays > 0 ? '${guestTtlDays}.${guestTtlTime}' : guestTtlTime
var guestRetentionHours = (guestDataTtlMinutes + 59) / 60
var base = '${namePrefix}-${environmentName}'
var uniq = take(uniqueString(resourceGroup().id), 5)
var tags = { app: 'agentwf', environment: environmentName }

var placeholderImage = 'mcr.microsoft.com/azuredocs/containerapps-helloworld:latest'
var placeholderPort = 80
var backendIsPlaceholder = empty(backendImage)
var frontendIsPlaceholder = empty(frontendImage)
var registryServer = '${acrName}${environment().suffixes.acrLoginServer}'

module monitoring 'modules/monitoring.bicep' = {
  name: 'monitoring'
  params: { name: base, location: location, tags: tags }
}

// Guest signing key alert + email action group (only when guests are on and an address is supplied).
module guestAlert 'modules/guest-alert.bicep' = if (guestOn && !empty(alertEmail)) {
  name: 'guest-alert'
  params: {
    name: base
    location: location
    tags: tags
    appInsightsId: monitoring.outputs.appInsightsId
    alertEmail: alertEmail
  }
}

module identity 'modules/identity.bicep' = {
  name: 'identity'
  params: { name: base, location: location, tags: tags }
}

// Dedicated ingestion identity: only exists when the job is deployed.
module ingestionIdentity 'modules/identity.bicep' = if (ingestionEnabled) {
  name: 'identity-ingestion'
  params: { name: '${base}-ingest', location: location, tags: tags }
}

module acr 'modules/acr.bicep' = if (createAcr) {
  name: 'acr'
  params: { name: acrName, location: location, tags: tags }
}

module acrPull 'modules/acr-pull.bicep' = if (assignAcrPull) {
  name: 'acr-pull'
  scope: resourceGroup(acrSubscriptionId, acrResourceGroup)
  params: { acrName: acrName, principalId: identity.outputs.principalId }
  dependsOn: [ acr ]
}

module acrPullIngestion 'modules/acr-pull.bicep' = if (assignAcrPull && ingestionEnabled) {
  name: 'acr-pull-ingestion'
  scope: resourceGroup(acrSubscriptionId, acrResourceGroup)
  params: { acrName: acrName, principalId: ingestionEnabled ? ingestionIdentity.?outputs.principalId ?? '' : '' }
  dependsOn: [ acr ]
}

module keyVault 'modules/keyvault.bicep' = {
  name: 'keyvault'
  params: {
    name: take('kv-${base}-${uniq}', 24)
    location: location
    tags: tags
    enablePurgeProtection: isProd
    kvReaderPrincipalId: useKeys ? identity.outputs.principalId : ''
    additionalSecretsUserPrincipalIds: useKeys && ingestionEnabled ? [ ingestionIdentity.?outputs.principalId ?? '' ] : []
    // Independent of the secrets role: prod has no Secrets User but needs Crypto User once guests are enabled.
    cryptoUserPrincipalId: guestOn ? identity.outputs.principalId : ''
    createGuestSigningKey: guestOn
    guestSigningKeyName: guestKeyName
  }
}

module cosmos 'modules/cosmos.bicep' = {
  name: 'cosmos'
  params: {
    name: 'cosmos-${base}-${uniq}'
    location: location
    tags: tags
    serverless: !isProd
    autoscaleMaxThroughput: cosmosAutoscaleMaxThroughput
    disableLocalAuth: isProd
    dataContributorPrincipalId: grantDataRoles ? identity.outputs.principalId : ''
  }
}

module search 'modules/search.bicep' = {
  name: 'search'
  params: {
    name: toLower('srch-${base}-${uniq}')
    location: searchLocation
    tags: tags
    sku: searchSku
    disableLocalAuth: isProd
    indexReaderPrincipalId: grantDataRoles ? identity.outputs.principalId : ''
    ingestionPrincipalId: isProd && ingestionEnabled ? ingestionIdentity.?outputs.principalId ?? '' : ''
  }
}

module secrets 'modules/keyvault-secrets.bicep' = if (useKeys) {
  name: 'keyvault-secrets'
  params: {
    vaultName: keyVault.outputs.name
    cosmosAccountName: cosmos.outputs.name
    searchServiceName: search.outputs.name
    writeSearchQueryKey: writeSearchQueryKeyFromBicep
    writeSearchAdminKey: ingestionEnabled
  }
}

// Foundry: "Azure AI User" for the app identity, only when a project is referenced.
var foundryParts = split(foundryProjectResourceId, '/')
module foundryRole 'modules/role-foundry-project.bicep' = if (!empty(foundryProjectResourceId)) {
  name: 'role-foundry'
  scope: resourceGroup(empty(foundryProjectResourceId) ? subscription().subscriptionId : foundryParts[2], empty(foundryProjectResourceId) ? resourceGroup().name : foundryParts[4])
  params: {
    accountName: empty(foundryProjectResourceId) ? 'unused' : foundryParts[8]
    projectName: empty(foundryProjectResourceId) ? 'unused' : foundryParts[10]
    principalId: identity.outputs.principalId
  }
}

// Search service identity -> embedding model resource (Phase 2, integrated vectorizer).
var modelParts = split(modelResourceId, '/')
module modelRole 'modules/role-model-account.bicep' = if (!empty(modelResourceId)) {
  name: 'role-model'
  scope: resourceGroup(empty(modelResourceId) ? subscription().subscriptionId : modelParts[2], empty(modelResourceId) ? resourceGroup().name : modelParts[4])
  params: {
    accountName: empty(modelResourceId) ? 'unused' : modelParts[8]
    principalId: search.outputs.principalId
  }
}

// Ingestion identity -> Foundry project (embeddings via AIProjectClient) and model account.
module foundryRoleIngestion 'modules/role-foundry-project.bicep' = if (ingestionEnabled && !empty(foundryProjectResourceId)) {
  name: 'role-foundry-ingestion'
  scope: resourceGroup(empty(foundryProjectResourceId) ? subscription().subscriptionId : foundryParts[2], empty(foundryProjectResourceId) ? resourceGroup().name : foundryParts[4])
  params: {
    accountName: empty(foundryProjectResourceId) ? 'unused' : foundryParts[8]
    projectName: empty(foundryProjectResourceId) ? 'unused' : foundryParts[10]
    principalId: ingestionIdentity.?outputs.principalId ?? ''
  }
}

module modelRoleIngestion 'modules/role-model-account.bicep' = if (ingestionEnabled && !empty(modelResourceId)) {
  name: 'role-model-ingestion'
  scope: resourceGroup(empty(modelResourceId) ? subscription().subscriptionId : modelParts[2], empty(modelResourceId) ? resourceGroup().name : modelParts[4])
  params: {
    accountName: empty(modelResourceId) ? 'unused' : modelParts[8]
    principalId: ingestionIdentity.?outputs.principalId ?? ''
  }
}

module containerEnv 'modules/container-env.bicep' = {
  name: 'container-env'
  params: { name: base, location: location, tags: tags, workspaceName: monitoring.outputs.workspaceName }
}

// ---- backend configuration -----------------------------------------------------------------------------------------
var baseEnv = [
  { name: 'ASPNETCORE_ENVIRONMENT', value: aspNetEnvironment }
  { name: 'ASPNETCORE_URLS', value: 'http://+:${containerPort}' }
  { name: 'AZURE_CLIENT_ID', value: identity.outputs.clientId }
  { name: 'AZURE_TOKEN_CREDENTIALS', value: 'ManagedIdentityCredential' }
  { name: 'AzureAd__Instance', value: entraInstance }
  { name: 'AzureAd__TenantId', value: entraTenantId }
  { name: 'AzureAd__ClientId', value: apiClientId }
  { name: 'Auth__RequiredScopes', value: requiredScopes }
  { name: 'Cosmos__Endpoint', value: cosmos.outputs.endpoint }
  { name: 'Cosmos__DatabaseName', value: cosmos.outputs.databaseName }
  { name: 'Cosmos__ContainerName', value: cosmos.outputs.containerName }
  { name: 'Search__Endpoint', value: search.outputs.endpoint }
  { name: 'Search__IndexName', value: searchIndexName }
  { name: 'Workflows__Echo__Enabled', value: echoWorkflowEnabled ? 'true' : 'false' }
  { name: 'Chat__RetentionDays', value: string(chatRetentionDays) }
  { name: 'APPLICATIONINSIGHTS_CONNECTION_STRING', secretRef: 'appinsights-connection' }
]
// Guest access (ADR 0006 section 11). When disabled only Guest__Enabled=false and Guest__DataTtl (leftover data retention) are set.
var guestEnv = concat(
  [
    { name: 'Guest__Enabled', value: guestOn ? 'true' : 'false' }
    { name: 'Guest__DataTtl', value: guestDataTtl }
  ],
  guestOn ? concat([
    { name: 'Guest__SigningMode', value: 'KeyVault' }
    { name: 'Guest__SigningKeyUri', value: '${keyVault.outputs.vaultUri}keys/${guestKeyName}' }
    { name: 'Guest__Issuer', value: 'agentwf-guest-${environmentName}' }
    { name: 'Guest__MaxLifetime', value: guestLifetime }
    { name: 'Guest__TrustClientIpHeader', value: 'true' }
  ],
    guestMaxCreationsPerHour == null ? [] : [ { name: 'Guest__MaxCreationsPerHour', value: string(guestMaxCreationsPerHour) } ],
    guestMaxTurns == null ? [] : [ { name: 'Guest__MaxTurns', value: string(guestMaxTurns) } ],
    guestMaxSessions == null ? [] : [ { name: 'Guest__MaxSessions', value: string(guestMaxSessions) } ],
    guestMaxTurnsPerHour == null ? [] : [ { name: 'Guest__MaxTurnsPerHour', value: string(guestMaxTurnsPerHour) } ],
    guestMaxTurnsPerDay == null ? [] : [ { name: 'Guest__MaxTurnsPerDay', value: string(guestMaxTurnsPerDay) } ]
  ) : []
)
var authModeEnv = [
  { name: 'Azure__AuthMode', value: useKeys ? 'Key' : 'ManagedIdentity' }
]
var foundryEnv = empty(foundryProjectEndpoint) ? [] : [
  { name: 'Foundry__ProjectEndpoint', value: foundryProjectEndpoint }
]
var workflowEnv = concat(
  empty(orchestratorDeployment) ? [] : [
    { name: 'Workflows__SupportTicket__Agents__Orchestrator__Deployment', value: orchestratorDeployment }
  ],
  empty(specialistDeployment) ? [] : [
    { name: 'Workflows__SupportTicket__Agents__Policy__Deployment', value: specialistDeployment }
    { name: 'Workflows__SupportTicket__Agents__Claims__Deployment', value: specialistDeployment }
    { name: 'Workflows__SupportTicket__Agents__HealthAssessments__Deployment', value: specialistDeployment }
  ]
)
var keyEnv = useKeys ? [
  { name: 'Cosmos__Key', secretRef: 'cosmos-key' }
  { name: 'Search__Key', secretRef: 'search-query-key' }
] : []

var keyVaultSecrets = useKeys ? [
  { name: 'cosmos-key', keyVaultUrl: '${keyVault.outputs.vaultUri}secrets/cosmos-key', identity: identity.outputs.id }
  { name: 'search-query-key', keyVaultUrl: '${keyVault.outputs.vaultUri}secrets/search-query-key', identity: identity.outputs.id }
] : []

module backend 'modules/containerapp.bicep' = {
  name: 'backend'
  params: {
    name: 'ca-${base}-backend'
    location: location
    tags: union(tags, { role: 'backend' })
    environmentId: containerEnv.outputs.id
    identityId: identity.outputs.id
    registryServer: registryServer
    image: backendIsPlaceholder ? placeholderImage : backendImage
    targetPort: backendIsPlaceholder ? placeholderPort : containerPort
    externalIngress: false // reachable only inside the environment, through the frontend nginx proxy
    minReplicas: backendMinReplicas
    maxReplicas: backendMaxReplicas
    concurrentRequests: backendConcurrentRequests
    enableHealthProbes: !backendIsPlaceholder
    env: concat(baseEnv, authModeEnv, foundryEnv, workflowEnv, keyEnv, guestEnv)
    secrets: keyVaultSecrets
    inlineSecretName: 'appinsights-connection'
    inlineSecretValue: monitoring.outputs.appInsightsConnectionString
  }
  dependsOn: [ acrPull, secrets ]
}

var frontendEnv = concat([
  { name: 'BACKEND_UPSTREAM', value: backend.outputs.fqdn }
  { name: 'AUTH_CLIENT_ID', value: spaClientId }
  { name: 'AUTH_AUTHORITY', value: spaAuthority }
  { name: 'AUTH_API_SCOPE', value: apiScope }
  { name: 'APP_RETENTION_DAYS', value: string(chatRetentionDays) }
  { name: 'AUTH_GUEST_ENABLED', value: guestOn ? 'true' : 'false' }
],
  guestOn ? [
    { name: 'APP_GUEST_SESSION_HOURS', value: string(guestMaxLifetimeHours) }
    { name: 'APP_GUEST_RETENTION_HOURS', value: string(guestRetentionHours) }
  ] : [],
  empty(authProviders) ? [] : [ { name: 'AUTH_PROVIDERS', value: authProviders } ],
  empty(supportContact) ? [] : [ { name: 'APP_SUPPORT_CONTACT', value: supportContact } ]
)

module frontend 'modules/containerapp.bicep' = {
  name: 'frontend'
  params: {
    name: 'ca-${base}-frontend'
    location: location
    tags: union(tags, { role: 'frontend' })
    environmentId: containerEnv.outputs.id
    identityId: identity.outputs.id
    registryServer: registryServer
    image: frontendIsPlaceholder ? placeholderImage : frontendImage
    targetPort: frontendIsPlaceholder ? placeholderPort : containerPort
    externalIngress: true
    minReplicas: frontendMinReplicas
    maxReplicas: frontendMaxReplicas
    concurrentRequests: frontendConcurrentRequests
    env: frontendEnv
  }
  dependsOn: [ acrPull ]
}

// Test only: same image, Azure__AuthMode=ManagedIdentity, no Cosmos__Key / Search__Key.
var smokeEnv = concat(
  filter(baseEnv, e => !contains(e, 'secretRef')),
  [ { name: 'Azure__AuthMode', value: 'ManagedIdentity' } ],
  foundryEnv,
  workflowEnv,
  guestEnv
)
module smokeJob 'modules/smoke-job.bicep' = if (isTest && deploySmokeJob && !backendIsPlaceholder) {
  name: 'smoke-job'
  params: {
    name: 'caj-${base}-smoke'
    location: location
    tags: tags
    environmentId: containerEnv.outputs.id
    identityId: identity.outputs.id
    registryServer: registryServer
    image: backendImage
    env: smokeEnv
  }
  dependsOn: [ acrPull ]
}

// Ingestion job (dev/test: Key mode with the admin key from Key Vault; prod: Managed Identity, no keys).
var effectiveVectorizerUri = !empty(vectorizerResourceUri) ? vectorizerResourceUri : (empty(modelResourceId) ? '' : 'https://${modelParts[8]}.openai.azure.com')
var ingestionEnv = concat([
  { name: 'ASPNETCORE_ENVIRONMENT', value: aspNetEnvironment }
  { name: 'DOTNET_ENVIRONMENT', value: aspNetEnvironment }
  { name: 'AZURE_CLIENT_ID', value: ingestionEnabled ? ingestionIdentity.?outputs.clientId ?? '' : '' }
  { name: 'AZURE_TOKEN_CREDENTIALS', value: 'ManagedIdentityCredential' }
  { name: 'Azure__AuthMode', value: useKeys ? 'Key' : 'ManagedIdentity' }
  { name: 'Search__Endpoint', value: search.outputs.endpoint }
  { name: 'Search__IndexName', value: searchIndexName }
  { name: 'Ingestion__SourcePath', value: ingestionSourcePath }
  { name: 'Ingestion__EmbeddingDeployment', value: embeddingDeployment }
  { name: 'Ingestion__VectorizerDeployment', value: embeddingDeployment }
],
  foundryEnv,
  empty(effectiveVectorizerUri) ? [] : [ { name: 'Ingestion__VectorizerResourceUri', value: effectiveVectorizerUri } ],
  useKeys ? [ { name: 'Ingestion__SearchAdminKey', secretRef: 'search-admin-key' } ] : []
)
module ingestionJob 'modules/ingestion-job.bicep' = if (ingestionEnabled) {
  name: 'ingestion-job'
  params: {
    name: 'caj-${base}-ingest'
    location: location
    tags: union(tags, { role: 'ingestion' })
    environmentId: containerEnv.outputs.id
    identityId: ingestionIdentity.?outputs.id ?? ''
    registryServer: registryServer
    image: ingestionImage
    env: ingestionEnv
    secrets: useKeys ? [
      { name: 'search-admin-key', keyVaultUrl: '${keyVault.outputs.vaultUri}secrets/search-admin-key', identity: ingestionIdentity.?outputs.id ?? '' }
    ] : []
  }
  dependsOn: [ acrPullIngestion, secrets ]
}

// ---- outputs (no secrets) ------------------------------------------------------------------------------------------
output resourceGroup string = resourceGroup().name
output acrName string = acrName
output acrLoginServer string = registryServer
output backendAppName string = backend.outputs.name
output frontendAppName string = frontend.outputs.name
output frontendFqdn string = frontend.outputs.fqdn
output managedIdentityClientId string = identity.outputs.clientId
output keyVaultName string = keyVault.outputs.name
output guestSigningKeyName string = keyVault.outputs.guestSigningKeyName
output cosmosAccountName string = cosmos.outputs.name
output searchServiceName string = search.outputs.name
output searchPrincipalId string = search.outputs.principalId
output smokeJobName string = smokeJob.?outputs.name ?? ''
output ingestionJobName string = ingestionJob.?outputs.name ?? ''
