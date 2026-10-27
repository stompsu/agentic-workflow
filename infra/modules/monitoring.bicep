// Log Analytics workspace + workspace-based Application Insights.
param name string
param location string
param tags object
param retentionDays int = 30

resource workspace 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: 'log-${name}'
  location: location
  tags: tags
  properties: {
    sku: { name: 'PerGB2018' }
    retentionInDays: retentionDays
  }
}

resource appInsights 'Microsoft.Insights/components@2020-02-02' = {
  name: 'appi-${name}'
  location: location
  tags: tags
  kind: 'web'
  properties: {
    Application_Type: 'web'
    WorkspaceResourceId: workspace.id
  }
}

output workspaceName string = workspace.name
output appInsightsId string = appInsights.id
// Marked secure so it is not recorded in deployment history; main.bicep passes it module-to-module only (never a main output).
@secure()
output appInsightsConnectionString string = appInsights.properties.ConnectionString
