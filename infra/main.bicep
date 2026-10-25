// Support chat infrastructure for ONE environment (dev | test | prod), deployed at resource-group scope:
//   az deployment group create -g <rg> --parameters infra/params/<env>.bicepparam
// See infra/README.md. No secret values pass through parameters or outputs.
targetScope = 'resourceGroup'

@allowed([ 'dev', 'test', 'prod' ])
param environmentName string
param location string = resourceGroup().location
@minLength(3)
@maxLength(12)
param namePrefix string = 'supportchat'

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

// ---- app configuration (non-secret; docs/plan.md section 5) --------------------------------------------------------
// Required (no defaults). Placeholders are rejected by scripts/check-params.sh (Bicep assert is experimental).
param entraInstance string
param entraTenantId string
param apiClientId string
param requiredScopes string = 'access_as_user'
param spaClientId string
param spaAuthority string
param apiScope string
param searchIndexName string = 'support-knowledge'
param echoWorkflowEnabled bool = false

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

// ---------------------------------------------------------------------------------------------------------------------
var isProd = environmentName == 'prod'
var isTest = environmentName == 'test'
var useKeys = !isProd                 // Key mode: dev and test (Azure__AuthMode=Key, KV secret references)
var grantDataRoles = isProd || isTest // data-plane roles for the app identity (test: for the MI smoke job)
var aspNetEnvironment = environmentName == 'dev' ? 'Dev' : (isTest ? 'Test' : 'Production')
var base = '${namePrefix}-${environmentName}'
var uniq = take(uniqueString(resourceGroup().id), 5)
var tags = { app: 'supportchat', environment: environmentName }

var placeholderImage = 'mcr.microsoft.com/azuredocs/containerapps-helloworld:latest'
var placeholderPort = 80
var backendIsPlaceholder = empty(backendImage)
var frontendIsPlaceholder = empty(frontendImage)
var registryServer = '${acrName}${environment().suffixes.acrLoginServer}'

module monitoring 'modules/monitoring.bicep' = {
  name: 'monitoring'
  params: { name: base, location: location, tags: tags }
}

module identity 'modules/identity.bicep' = {
  name: 'identity'
  params: { name: base, location: location, tags: tags }
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

module keyVault 'modules/keyvault.bicep' = {
  name: 'keyvault'
  params: {
    name: take('kv-${base}-${uniq}', 24)
    location: location
    tags: tags
    enablePurgeProtection: isProd
    kvReaderPrincipalId: useKeys ? identity.outputs.principalId : ''
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
    location: location
    tags: tags
    sku: searchSku
    disableLocalAuth: isProd
    indexReaderPrincipalId: grantDataRoles ? identity.outputs.principalId : ''
  }
}

module secrets 'modules/keyvault-secrets.bicep' = if (useKeys) {
  name: 'keyvault-secrets'
  params: {
    vaultName: keyVault.outputs.name
    cosmosAccountName: cosmos.outputs.name
    searchServiceName: search.outputs.name
    writeSearchQueryKey: writeSearchQueryKeyFromBicep
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
  { name: 'Workflows__Echo__Enabled', value: string(echoWorkflowEnabled) }
  { name: 'APPLICATIONINSIGHTS_CONNECTION_STRING', secretRef: 'appinsights-connection' }
]
var authModeEnv = [
  { name: 'Azure__AuthMode', value: useKeys ? 'Key' : 'ManagedIdentity' }
]
var foundryEnv = empty(foundryProjectEndpoint) ? [] : [
  { name: 'Foundry__ProjectEndpoint', value: foundryProjectEndpoint }
]
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
    env: concat(baseEnv, authModeEnv, foundryEnv, keyEnv)
    secrets: keyVaultSecrets
    inlineSecretName: 'appinsights-connection'
    inlineSecretValue: monitoring.outputs.appInsightsConnectionString
  }
  dependsOn: [ acrPull, secrets ]
}

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
    env: [
      { name: 'BACKEND_UPSTREAM', value: backend.outputs.fqdn }
      { name: 'AUTH_CLIENT_ID', value: spaClientId }
      { name: 'AUTH_AUTHORITY', value: spaAuthority }
      { name: 'AUTH_API_SCOPE', value: apiScope }
    ]
  }
  dependsOn: [ acrPull ]
}

// Test only: same image, Azure__AuthMode=ManagedIdentity, no Cosmos__Key / Search__Key.
var smokeEnv = concat(
  filter(baseEnv, e => !contains(e, 'secretRef')),
  [ { name: 'Azure__AuthMode', value: 'ManagedIdentity' } ],
  foundryEnv
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

// ---- outputs (no secrets) ------------------------------------------------------------------------------------------
output resourceGroup string = resourceGroup().name
output acrName string = acrName
output acrLoginServer string = registryServer
output backendAppName string = backend.outputs.name
output frontendAppName string = frontend.outputs.name
output frontendFqdn string = frontend.outputs.fqdn
output managedIdentityClientId string = identity.outputs.clientId
output keyVaultName string = keyVault.outputs.name
output cosmosAccountName string = cosmos.outputs.name
output searchServiceName string = search.outputs.name
output searchPrincipalId string = search.outputs.principalId
output smokeJobName string = smokeJob.?outputs.name ?? ''
