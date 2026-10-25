// Generic Container App (used for backend and frontend). One revision at a time; the pipeline sets revision suffixes.
param name string
param location string
param tags object
param environmentId string
param identityId string
param registryServer string
param image string
param targetPort int
@description('true = public ingress (frontend). false = environment-internal ingress (backend).')
param externalIngress bool
param minReplicas int
param maxReplicas int
@description('Concurrent HTTP requests per replica that trigger scale-out.')
param concurrentRequests int
@description('Backend probes: startup + liveness on /healthz, readiness on /readyz.')
param enableHealthProbes bool = false
param cpu string = '0.5'
param memory string = '1Gi'
param env array = []
@description('Key Vault-reference secrets (no secret values).')
param secrets array = []
@description('Optional inline secret value stored as the Container Apps secret named by inlineSecretName.')
@secure()
param inlineSecretValue string = ''
param inlineSecretName string = 'appinsights-connection'

var inlineSecrets = empty(inlineSecretValue) ? [] : [
  { name: inlineSecretName, value: inlineSecretValue }
]

var probes = enableHealthProbes ? [
  {
    type: 'Startup'
    httpGet: { path: '/healthz', port: targetPort }
    periodSeconds: 5
    failureThreshold: 30
    timeoutSeconds: 3
  }
  {
    type: 'Liveness'
    httpGet: { path: '/healthz', port: targetPort }
    periodSeconds: 15
    failureThreshold: 3
    timeoutSeconds: 3
  }
  {
    type: 'Readiness'
    httpGet: { path: '/readyz', port: targetPort }
    periodSeconds: 10
    failureThreshold: 3
    timeoutSeconds: 3
  }
] : []

resource app 'Microsoft.App/containerApps@2025-01-01' = {
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
      activeRevisionsMode: 'Single'
      ingress: {
        external: externalIngress
        targetPort: targetPort
        transport: 'auto'
        allowInsecure: false
      }
      registries: [
        { server: registryServer, identity: identityId }
      ]
      secrets: concat(inlineSecrets, secrets)
    }
    template: {
      containers: [
        {
          name: 'app'
          image: image
          resources: { cpu: json(cpu), memory: memory }
          env: env
          probes: probes
        }
      ]
      scale: {
        minReplicas: minReplicas
        maxReplicas: maxReplicas
        rules: [
          {
            name: 'http-concurrency'
            http: { metadata: { concurrentRequests: string(concurrentRequests) } }
          }
        ]
      }
    }
  }
}

output name string = app.name
output fqdn string = app.properties.configuration.ingress.fqdn
