// Azure AI Search. The index itself is created by ingestion/script, not Bicep (docs/plan.md section 3).
param name string
param location string
param tags object
@allowed([ 'basic', 'standard', 'standard2', 'standard3' ])
param sku string = 'basic' // semantic ranker needs Basic or higher (data-model 2.3)
@description('true in prod: API keys disabled, Entra only. false = aadOrApiKey (dev/test).')
param disableLocalAuth bool
@description('Principal that receives Search Index Data Reader (prod and test). Empty = none.')
param indexReaderPrincipalId string = ''
@description('Ingestion identity: Search Index Data Contributor + Search Service Contributor (create/update the index). prod only; dev/test ingestion uses the admin key. Empty = none.')
param ingestionPrincipalId string = ''

var indexDataReaderRoleId = '1407120a-92aa-4202-b7e9-c0e197c71c8f'

resource search 'Microsoft.Search/searchServices@2025-05-01' = {
  name: name
  location: location
  tags: tags
  sku: { name: sku }
  // System-assigned identity is used by the integrated vectorizer (needs Cognitive Services OpenAI User, Phase 2)
  identity: { type: 'SystemAssigned' }
  properties: {
    replicaCount: 1
    partitionCount: 1
    hostingMode: 'Default'
    publicNetworkAccess: 'enabled'
    semanticSearch: 'standard'
    disableLocalAuth: disableLocalAuth
    // authOptions and disableLocalAuth are mutually exclusive
    authOptions: disableLocalAuth ? null : {
      aadOrApiKey: { aadAuthFailureMode: 'http401WithBearerChallenge' }
    }
  }
}

resource readerRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (!empty(indexReaderPrincipalId)) {
  name: guid(search.id, indexReaderPrincipalId, indexDataReaderRoleId)
  scope: search
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', indexDataReaderRoleId)
    principalId: indexReaderPrincipalId
    principalType: 'ServicePrincipal'
  }
}

var indexDataContributorRoleId = '8ebe5a00-799e-43f5-93ac-243d3dce84a7'
var searchServiceContributorRoleId = '7ca78c08-252a-4471-8644-bb5ff32d4ba0'

resource ingestIndexData 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (!empty(ingestionPrincipalId)) {
  name: guid(search.id, ingestionPrincipalId, indexDataContributorRoleId)
  scope: search
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', indexDataContributorRoleId)
    principalId: ingestionPrincipalId
    principalType: 'ServicePrincipal'
  }
}

resource ingestService 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (!empty(ingestionPrincipalId)) {
  name: guid(search.id, ingestionPrincipalId, searchServiceContributorRoleId)
  scope: search
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', searchServiceContributorRoleId)
    principalId: ingestionPrincipalId
    principalType: 'ServicePrincipal'
  }
}

output name string = search.name
output endpoint string = 'https://${search.name}.search.windows.net'
output principalId string = search.identity.principalId
