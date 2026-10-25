// Manual-trigger Container Apps Job that runs the ingestion console app (chunk + embed + upsert into AI Search).
// Separate from the web apps. Uses a dedicated identity (least privilege). Start it with:
//   az containerapp job start -g <rg> -n <ingestionJobName>
param name string
param location string
param tags object
param environmentId string
param identityId string
param registryServer string
param image string
param env array
param secrets array = []
param replicaTimeout int = 1800

resource job 'Microsoft.App/jobs@2025-01-01' = {
  name: name
  location: location
  tags: tags
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: { '${identityId}': {} }
  }
  properties: {
    environmentId: environmentId
    workloadProfileName: 'Consumption'
    configuration: {
      triggerType: 'Manual'
      replicaTimeout: replicaTimeout
      replicaRetryLimit: 0
      manualTriggerConfig: { parallelism: 1, replicaCompletionCount: 1 }
      registries: [
        { server: registryServer, identity: identityId }
      ]
      secrets: secrets
    }
    template: {
      containers: [
        {
          name: 'ingestion'
          image: image
          resources: { cpu: json('0.5'), memory: '1Gi' }
          env: env
        }
      ]
    }
  }
}

output name string = job.name
