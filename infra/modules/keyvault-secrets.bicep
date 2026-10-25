// Dev/test only. Secret values are produced inside the deployment and written straight into Key Vault.
// They are never parameters or outputs. Deploying identity needs "Key Vault Secrets Officer" on the vault (or RG).
param vaultName string
param cosmosAccountName string
param searchServiceName string
param writeSearchQueryKey bool

resource vault 'Microsoft.KeyVault/vaults@2023-07-01' existing = {
  name: vaultName
}

resource cosmos 'Microsoft.DocumentDB/databaseAccounts@2025-04-15' existing = {
  name: cosmosAccountName
}

resource search 'Microsoft.Search/searchServices@2025-05-01' existing = {
  name: searchServiceName
}

resource cosmosKey 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = {
  parent: vault
  name: 'cosmos-key'
  properties: {
    value: cosmos.listKeys().primaryMasterKey
    attributes: { enabled: true }
  }
}

// listQueryKeys() returns the service's query keys; the default (unnamed) query key is value[0].
// Read-only key. The admin key is never read here. Set writeSearchQueryKey=false to use
// scripts/ensure-search-query-key.sh instead (see README).
resource searchQueryKey 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = if (writeSearchQueryKey) {
  parent: vault
  name: 'search-query-key'
  properties: {
    value: search.listQueryKeys().value[0].key
    attributes: { enabled: true }
  }
}

output cosmosKeySecretUri string = '${vault.properties.vaultUri}secrets/${cosmosKey.name}'
output searchQueryKeySecretUri string = '${vault.properties.vaultUri}secrets/search-query-key'
