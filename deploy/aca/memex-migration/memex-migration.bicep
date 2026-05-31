@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

param memex_aca_outputs_azure_container_apps_environment_default_domain string

param memex_aca_outputs_azure_container_apps_environment_id string

@secure()
param memex_postgres_password_value string

resource memex_migration 'Microsoft.App/containerApps@2025-07-01' = {
  name: 'memex-migration'
  location: location
  properties: {
    configuration: {
      secrets: [
        {
          name: 'connectionstrings--memex'
          value: 'Host=memex-postgres;Port=5432;Username=postgres;Password=${memex_postgres_password_value};Database=memex'
        }
        {
          name: 'memex-password'
          value: memex_postgres_password_value
        }
        {
          name: 'memex-uri'
          value: 'postgresql://postgres:${uriComponent(memex_postgres_password_value)}@memex-postgres:5432/memex'
        }
      ]
      activeRevisionsMode: 'Single'
    }
    environmentId: memex_aca_outputs_azure_container_apps_environment_id
    template: {
      containers: [
        {
          image: 'ghcr.io/systemorph/memex-migration:latest'
          name: 'memex-migration'
          env: [
            {
              name: 'ConnectionStrings__memex'
              secretRef: 'connectionstrings--memex'
            }
            {
              name: 'MEMEX_HOST'
              value: 'memex-postgres'
            }
            {
              name: 'MEMEX_PORT'
              value: '5432'
            }
            {
              name: 'MEMEX_USERNAME'
              value: 'postgres'
            }
            {
              name: 'MEMEX_PASSWORD'
              secretRef: 'memex-password'
            }
            {
              name: 'MEMEX_URI'
              secretRef: 'memex-uri'
            }
            {
              name: 'MEMEX_JDBCCONNECTIONSTRING'
              value: 'jdbc:postgresql://memex-postgres:5432/memex'
            }
            {
              name: 'MEMEX_DATABASENAME'
              value: 'memex'
            }
          ]
        }
      ]
      scale: {
        minReplicas: 1
      }
    }
  }
}