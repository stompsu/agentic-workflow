// Key Vault in RBAC mode. The app identity gets Key Vault Secrets User only when Key-mode secrets are used (dev/test).
param name string
param location string
param tags object
param enablePurgeProtection bool
param kvReaderPrincipalId string = '' // empty = no role assignment (prod)
@description('Further principals (e.g. the ingestion job identity) that receive Key Vault Secrets User. dev/test only.')
param additionalSecretsUserPrincipalIds array = []

var secretsUserRoleId = '4633458b-17de-408a-b874-0445c86b69e6'

resource vault 'Microsoft.KeyVault/vaults@2023-07-01' = {
  name: name
  location: location
  tags: tags
  properties: {
    tenantId: tenant().tenantId
    sku: { family: 'A', name: 'standard' }
    enableRbacAuthorization: true
    enableSoftDelete: true
    softDeleteRetentionInDays: 90
    // purge protection cannot be turned off once on; only enabled for prod
    enablePurgeProtection: enablePurgeProtection ? true : null
    publicNetworkAccess: 'Enabled'
  }
}

resource secretsUser 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (!empty(kvReaderPrincipalId)) {
  name: guid(vault.id, kvReaderPrincipalId, secretsUserRoleId)
  scope: vault
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', secretsUserRoleId)
    principalId: kvReaderPrincipalId
    principalType: 'ServicePrincipal'
  }
}

resource extraSecretsUsers 'Microsoft.Authorization/roleAssignments@2022-04-01' = [for principalId in additionalSecretsUserPrincipalIds: {
  name: guid(vault.id, principalId, secretsUserRoleId)
  scope: vault
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', secretsUserRoleId)
    principalId: principalId
    principalType: 'ServicePrincipal'
  }
}]

output name string = vault.name
output vaultUri string = vault.properties.vaultUri
