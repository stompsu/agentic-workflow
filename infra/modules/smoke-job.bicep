// Test only (ADR 0003 section 1a): manual-trigger Container Apps Job running the SAME backend image with
// Azure__AuthMode=ManagedIdentity and no keys, command arg --smoke-check. The pipeline starts it after the test deploy.
param name string
param location string
param tags object
param environmentId string
param identityId string
param registryServer string
param image string
param env array

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
      replicaTimeout: 300
      replicaRetryLimit: 0
      manualTriggerConfig: { parallelism: 1, replicaCompletionCount: 1 }
      registries: [
        { server: registryServer, identity: identityId }
      ]
    }
    template: {
      containers: [
        {
          name: 'smoke'
          image: image
          args: [ '--smoke-check' ]
          resources: { cpu: json('0.25'), memory: '0.5Gi' }
          env: env
        }
      ]
    }
  }
}

output name string = job.name
