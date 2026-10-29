// Cosmos DB (NoSQL): database + single container per docs/data-model.md section 1.
param name string
param location string
param tags object
param databaseName string = 'agentwf'
param containerName string = 'chat'
@description('Serverless (dev/test) or provisioned autoscale (prod).')
param serverless bool
param autoscaleMaxThroughput int = 4000
@description('true in prod: keys disabled, Entra only.')
param disableLocalAuth bool
@description('Principal that receives the data-plane Built-in Data Contributor role (prod and test). Empty = none.')
param dataContributorPrincipalId string = ''

// Built-in Cosmos DB Data Contributor (data-plane role, not an ARM role)
var dataContributorRoleId = '00000000-0000-0000-0000-000000000002'

resource account 'Microsoft.DocumentDB/databaseAccounts@2025-04-15' = {
  name: name
  location: location
  tags: tags
  kind: 'GlobalDocumentDB'
  properties: {
    databaseAccountOfferType: 'Standard'
    consistencyPolicy: { defaultConsistencyLevel: 'Session' }
    locations: [
      { locationName: location, failoverPriority: 0, isZoneRedundant: false }
    ]
    capabilities: serverless ? [ { name: 'EnableServerless' } ] : []
    disableLocalAuth: disableLocalAuth
    minimalTlsVersion: 'Tls12'
    publicNetworkAccess: 'Enabled'
  }
}

resource database 'Microsoft.DocumentDB/databaseAccounts/sqlDatabases@2025-04-15' = {
  parent: account
  name: databaseName
  properties: {
    resource: { id: databaseName }
  }
}

resource container 'Microsoft.DocumentDB/databaseAccounts/sqlDatabases/containers@2025-04-15' = {
  parent: database
  name: containerName
  properties: {
    resource: {
      id: containerName
      partitionKey: { paths: [ '/userId' ], kind: 'Hash', version: 2 }
      defaultTtl: -1 // TTL enabled, no default expiry; per-document ttl set by the app (data-model 1.3)
      indexingPolicy: {
        indexingMode: 'consistent'
        automatic: true
        includedPaths: [
          { path: '/userId/?' }
          { path: '/type/?' }
          { path: '/sessionId/?' }
          { path: '/state/?' }
          { path: '/status/?' }
          { path: '/createdAt/?' }
          { path: '/updatedAt/?' }
        ]
        excludedPaths: [ { path: '/*' } ]
        compositeIndexes: [
          [
            { path: '/userId', order: 'ascending' }
            { path: '/type', order: 'ascending' }
            { path: '/state', order: 'ascending' }
            { path: '/updatedAt', order: 'descending' }
          ]
          [
            { path: '/userId', order: 'ascending' }
            { path: '/type', order: 'ascending' }
            { path: '/sessionId', order: 'ascending' }
            { path: '/createdAt', order: 'descending' }
          ]
        ]
      }
    }
    // Serverless accounts reject throughput settings.
    options: serverless ? {} : { autoscaleSettings: { maxThroughput: autoscaleMaxThroughput } }
  }
}

resource dataRole 'Microsoft.DocumentDB/databaseAccounts/sqlRoleAssignments@2025-04-15' = if (!empty(dataContributorPrincipalId)) {
  parent: account
  name: guid(account.id, dataContributorPrincipalId, dataContributorRoleId, databaseName)
  properties: {
    roleDefinitionId: '${account.id}/sqlRoleDefinitions/${dataContributorRoleId}'
    principalId: dataContributorPrincipalId
    scope: '${account.id}/dbs/${databaseName}'
  }
  dependsOn: [ database ]
}

output name string = account.name
output endpoint string = account.properties.documentEndpoint
output databaseName string = databaseName
output containerName string = containerName
