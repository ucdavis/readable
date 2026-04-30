@description('OpenDataLoader worker container app name.')
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
@description('Service Bus connection string used by the OpenDataLoader worker.')
param serviceBusConnectionString string

@secure()
@description('Storage connection string used by the OpenDataLoader worker.')
param storageConnectionString string

@description('Queue consumed by the OpenDataLoader worker.')
param autotagQueueName string = 'autotag-odl'

@description('Queue that receives PDF finalization messages.')
param finalizeQueueName string = 'pdf-finalize'

@description('Queue that receives terminal autotag failure messages.')
param failedQueueName string = 'pdf-failed'

@description('OpenDataLoader process timeout in seconds.')
param processTimeoutSeconds int = 210

@minValue(1)
@description('Maximum Service Bus delivery count before the worker reports failure to ingest.')
param maxDeliveryCount int = 10

@minValue(1)
@description('Maximum conversions to run concurrently inside one replica.')
param maxConcurrentConversions int = 1

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
      registries: [
        {
          server: registryServer
          identity: registryIdentityResourceId
        }
      ]
      secrets: [
        {
          name: 'servicebus-connection-string'
          value: serviceBusConnectionString
        }
        {
          name: 'storage-connection-string'
          value: storageConnectionString
        }
      ]
    }
    template: {
      containers: [
        {
          name: 'opendataloader-worker'
          image: image
          env: [
            {
              name: 'ServiceBus'
              secretRef: 'servicebus-connection-string'
            }
            {
              name: 'Storage__ConnectionString'
              secretRef: 'storage-connection-string'
            }
            {
              name: 'ODL_AUTOTAG_QUEUE_NAME'
              value: autotagQueueName
            }
            {
              name: 'ODL_FINALIZE_QUEUE_NAME'
              value: finalizeQueueName
            }
            {
              name: 'ODL_FAILED_QUEUE_NAME'
              value: failedQueueName
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
              name: 'ODL_MAX_DELIVERY_COUNT'
              value: string(maxDeliveryCount)
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
            name: 'servicebus-autotag-queue'
            custom: {
              type: 'azure-servicebus'
              metadata: {
                queueName: autotagQueueName
                messageCount: '1'
              }
              auth: [
                {
                  secretRef: 'servicebus-connection-string'
                  triggerParameter: 'connection'
                }
              ]
            }
          }
        ]
      }
    }
  }
}

output name string = containerApp.name
