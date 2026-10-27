// Key Vault in RBAC mode. The app identity gets Key Vault Secrets User only when Key-mode secrets are used (dev/test).
param name string
param location string
param tags object
param enablePurgeProtection bool
param kvReaderPrincipalId string = '' // empty = no role assignment (prod)
@description('Further principals (e.g. the ingestion job identity) that receive Key Vault Secrets User. dev/test only.')
param additionalSecretsUserPrincipalIds array = []
@description('Guest access (ADR 0006 section 12): principal that receives Key Vault Crypto User (sign/verify with keys, no key read). Independent of kvReaderPrincipalId, so prod (no secrets role) can have it. Empty = no assignment.')
param cryptoUserPrincipalId string = ''
@description('Create the guest token signing key (EC P-256, sign/verify). Set only when guest access is enabled.')
param createGuestSigningKey bool = false
param guestSigningKeyName string = 'guest-token-signing'

var secretsUserRoleId = '4633458b-17de-408a-b874-0445c86b69e6'
var cryptoUserRoleId = '12338af0-0e69-4776-bea7-57ae8d297424' // Key Vault Crypto User

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

resource cryptoUser 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (!empty(cryptoUserPrincipalId)) {
  name: guid(vault.id, cryptoUserPrincipalId, cryptoUserRoleId)
  scope: vault
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', cryptoUserRoleId)
    principalId: cryptoUserPrincipalId
    principalType: 'ServicePrincipal'
  }
}

// Guest token signing key (ADR 0006 sections 5.4, 12). Bicep never creates new versions on redeploy. The initial version has
// no expiry (attributes.exp unset); the rotation policy's expiryTime applies only to versions created by rotation (G13).
resource guestSigningKey 'Microsoft.KeyVault/vaults/keys@2023-07-01' = if (createGuestSigningKey) {
  parent: vault
  name: guestSigningKeyName
  tags: tags
  properties: {
    kty: 'EC'
    curveName: 'P-256'
    keyOps: [ 'sign', 'verify' ]
    attributes: { enabled: true }
    rotationPolicy: {
      attributes: { expiryTime: 'P1Y' }
      lifetimeActions: [
        { trigger: { timeAfterCreate: 'P90D' }, action: { type: 'rotate' } }
        { trigger: { timeBeforeExpiry: 'P30D' }, action: { type: 'notify' } }
      ]
    }
  }
}

output name string = vault.name
output vaultUri string = vault.properties.vaultUri
output guestSigningKeyName string = createGuestSigningKey ? guestSigningKeyName : ''
