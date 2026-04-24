@description('OpenDataLoader container app name.')
param name string

@description('Azure region for the container app.')
param location string

@description('Tags to apply to the container app.')
param tags object

@description('Container Apps environment resource ID.')
param environmentId string

@description('Container image reference.')
param image string

@description('Container Registry login server.')
param registryServer string

@description('User-assigned managed identity resource ID used to pull from the container registry.')
param registryIdentityResourceId string

@secure()
@description('Shared secret required on X-Api-Key.')
param apiKey string

@description('Maximum request body size in MB.')
param maxRequestBodySizeMb int = 50

@description('OpenDataLoader process timeout in seconds.')
param processTimeoutSeconds int = 210

@minValue(1)
@description('Maximum conversions to run concurrently inside one replica.')
param maxConcurrentConversions int = 1

@minValue(0)
@description('Maximum requests to hold in the in-memory queue inside one replica.')
param maxQueuedConversions int = 20

@minValue(1)
@description('Maximum seconds a request may wait in the in-memory queue before returning 429.')
param queueTimeoutSeconds int = 60

@minValue(1)
@description('HTTP concurrent request target for Container Apps autoscale.')
param httpConcurrentRequests int = 1

@description('CPU allocation for the container app.')
param cpu string = '2.0'

@description('Memory allocation for the container app.')
param memory string = '4Gi'

@description('Minimum replica count.')
param minReplicas int = 1

@description('Maximum replica count.')
param maxReplicas int = 20

resource containerApp 'Microsoft.App/containerApps@2024-03-01' = {
  name: name
  location: location
  tags: tags
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${registryIdentityResourceId}': {}
    }
  }
  properties: {
    managedEnvironmentId: environmentId
    configuration: {
      activeRevisionsMode: 'Single'
      ingress: {
        external: true
        allowInsecure: false
        targetPort: 8080
        transport: 'auto'
      }
      registries: [
        {
          server: registryServer
          identity: registryIdentityResourceId
        }
      ]
      secrets: [
        {
          name: 'api-key'
          value: apiKey
        }
      ]
    }
    template: {
      containers: [
        {
          name: 'opendataloader-api'
          image: image
          env: [
            {
              name: 'ASPNETCORE_URLS'
              value: 'http://0.0.0.0:8080'
            }
            {
              name: 'ODL_SHARED_SECRET'
              secretRef: 'api-key'
            }
            {
              name: 'ODL_MAX_REQUEST_BODY_SIZE_MB'
              value: string(maxRequestBodySizeMb)
            }
            {
              name: 'ODL_PROCESS_TIMEOUT_SECONDS'
              value: string(processTimeoutSeconds)
            }
            {
              name: 'ODL_MAX_CONCURRENT_CONVERSIONS'
              value: string(maxConcurrentConversions)
            }
            {
              name: 'ODL_MAX_QUEUED_CONVERSIONS'
              value: string(maxQueuedConversions)
            }
            {
              name: 'ODL_QUEUE_TIMEOUT_SECONDS'
              value: string(queueTimeoutSeconds)
            }
          ]
          probes: [
            {
              type: 'Startup'
              httpGet: {
                path: '/health'
                port: 8080
              }
              initialDelaySeconds: 5
              periodSeconds: 10
              failureThreshold: 18
              timeoutSeconds: 3
            }
            {
              type: 'Liveness'
              httpGet: {
                path: '/health'
                port: 8080
              }
              initialDelaySeconds: 20
              periodSeconds: 30
              failureThreshold: 3
              timeoutSeconds: 3
            }
            {
              type: 'Readiness'
              httpGet: {
                path: '/health'
                port: 8080
              }
              initialDelaySeconds: 10
              periodSeconds: 15
              failureThreshold: 3
              timeoutSeconds: 3
            }
          ]
          resources: {
            cpu: json(cpu)
            memory: memory
          }
        }
      ]
      scale: {
        minReplicas: minReplicas
        maxReplicas: maxReplicas
        rules: [
          {
            name: 'http-concurrency'
            http: {
              metadata: {
                concurrentRequests: string(httpConcurrentRequests)
              }
            }
          }
        ]
      }
    }
  }
}

output name string = containerApp.name
output fqdn string = containerApp.properties.configuration.ingress.fqdn
