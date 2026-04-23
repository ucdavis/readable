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

@description('Container Registry username.')
param registryUsername string

@secure()
@description('Container Registry password.')
param registryPassword string

@secure()
@description('Shared secret required on X-Api-Key.')
param apiKey string

@description('Maximum request body size in MB.')
param maxRequestBodySizeMb int = 50

@description('OpenDataLoader process timeout in seconds.')
param processTimeoutSeconds int = 210

@description('CPU allocation for the container app.')
param cpu string = '2.0'

@description('Memory allocation for the container app.')
param memory string = '4Gi'

@description('Minimum replica count.')
param minReplicas int = 1

@description('Maximum replica count.')
param maxReplicas int = 3

resource containerApp 'Microsoft.App/containerApps@2024-03-01' = {
  name: name
  location: location
  tags: tags
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
          username: registryUsername
          passwordSecretRef: 'registry-password'
        }
      ]
      secrets: [
        {
          name: 'registry-password'
          value: registryPassword
        }
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
            cpu: any(cpu)
            memory: memory
          }
        }
      ]
      scale: {
        minReplicas: minReplicas
        maxReplicas: maxReplicas
      }
    }
  }
}

output name string = containerApp.name
output fqdn string = containerApp.properties.configuration.ingress.fqdn

