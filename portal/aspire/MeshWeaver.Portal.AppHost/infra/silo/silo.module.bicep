@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

param silo_containerport string

param meshweaverblobs_outputs_tableendpoint string

param mesh_cluster_id_value string

param mesh_service_id_value string

param azure_postgres_outputs_connectionstring string

param outputs_azure_container_registry_managed_identity_id string

param outputs_managed_identity_client_id string

param outputs_azure_container_apps_environment_id string

param outputs_azure_container_registry_endpoint string

param silo_containerimage string

resource silo 'Microsoft.App/containerApps@2024-03-01' = {
  name: 'silo'
  location: location
  properties: {
    configuration: {
      activeRevisionsMode: 'Single'
      ingress: {
        external: false
        targetPort: silo_containerport
        transport: 'http'
        additionalPortMappings: [
          {
            external: false
            targetPort: 8000
          }
          {
            external: false
            targetPort: 8001
          }
        ]
      }
      registries: [
        {
          server: outputs_azure_container_registry_endpoint
          identity: outputs_azure_container_registry_managed_identity_id
        }
      ]
    }
    environmentId: outputs_azure_container_apps_environment_id
    template: {
      containers: [
        {
          image: silo_containerimage
          name: 'silo'
          resources: {
            cpu: 2
            memory: '4Gi'
          }
          env: [
            {
              name: 'OTEL_DOTNET_EXPERIMENTAL_OTLP_EMIT_EXCEPTION_LOG_ATTRIBUTES'
              value: 'true'
            }
            {
              name: 'OTEL_DOTNET_EXPERIMENTAL_OTLP_EMIT_EVENT_LOG_ATTRIBUTES'
              value: 'true'
            }
            {
              name: 'OTEL_DOTNET_EXPERIMENTAL_OTLP_RETRY'
              value: 'in_memory'
            }
            {
              name: 'ASPNETCORE_FORWARDEDHEADERS_ENABLED'
              value: 'true'
            }
            {
              name: 'HTTP_PORTS'
              value: silo_containerport
            }
            {
              name: 'Orleans__Clustering__ProviderType'
              value: 'AzureTableStorage'
            }
            {
              name: 'Orleans__Clustering__ServiceKey'
              value: 'orleans-clustering'
            }
            {
              name: 'ConnectionStrings__orleans-clustering'
              value: meshweaverblobs_outputs_tableendpoint
            }
            {
              name: 'Orleans__ClusterId'
              value: mesh_cluster_id_value
            }
            {
              name: 'Orleans__ServiceId'
              value: mesh_service_id_value
            }
            {
              name: 'Orleans__EnableDistributedTracing'
              value: 'true'
            }
            {
              name: 'Orleans__Endpoints__SiloPort'
              value: '8000'
            }
            {
              name: 'Orleans__Endpoints__GatewayPort'
              value: '8001'
            }
            {
              name: 'ConnectionStrings__meshweaverdb'
              value: '${azure_postgres_outputs_connectionstring};Database=meshweaverdb'
            }
            {
              name: 'AZURE_CLIENT_ID'
              value: outputs_managed_identity_client_id
            }
          ]
        }
      ]
      scale: {
        minReplicas: 1
      }
    }
  }
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${outputs_azure_container_registry_managed_identity_id}': { }
    }
  }
}