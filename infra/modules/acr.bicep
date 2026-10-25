// Shared Azure Container Registry. Created only in the environment that owns it (dev); test/prod reference it by name.
param name string
param location string
param tags object
param sku string = 'Standard'

resource acr 'Microsoft.ContainerRegistry/registries@2023-07-01' = {
  name: name
  location: location
  tags: tags
  sku: { name: sku }
  properties: {
    adminUserEnabled: false // identity-based pull only (AcrPull on the user-assigned MI)
  }
}

output name string = acr.name
output loginServer string = acr.properties.loginServer
