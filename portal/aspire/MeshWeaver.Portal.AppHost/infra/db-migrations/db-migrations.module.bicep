@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

param db_migrations_containerport string

param azure_postgres_outputs_connectionstring string

param outputs_azure_container_registry_managed_identity_id string

param outputs_managed_identity_client_id string

param outputs_azure_container_apps_environment_id string

param outputs_azure_container_registry_endpoint string

param db_migrations_containerimage string

resource db_migrations 'Microsoft.App/containerApps@2024-03-01' = {
  name: 'db-migrations'
  location: location
  properties: {
    configuration: {
      activeRevisionsMode: 'Single'
      ingress: {
        external: false
        targetPort: db_migrations_containerport
        transport: 'http'
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
          image: db_migrations_containerimage
          name: 'db-migrations'
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
              value: db_migrations_containerport
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