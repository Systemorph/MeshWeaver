@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

param memex_aca_outputs_azure_container_apps_environment_default_domain string

param memex_aca_outputs_azure_container_apps_environment_id string

@secure()
param memex_postgres_password_value string

param memex_aca_outputs_volumes_memex_portal_0 string

param memex_aca_outputs_volumes_memex_portal_1 string

resource memex_portal 'Microsoft.App/containerApps@2025-07-01' = {
  name: 'memex-portal'
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
      ingress: {
        external: true
        targetPort: 8080
        transport: 'http'
      }
    }
    environmentId: memex_aca_outputs_azure_container_apps_environment_id
    template: {
      containers: [
        {
          image: 'ghcr.io/systemorph/memex-portal-ai:latest'
          name: 'memex-portal'
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
            {
              name: 'ASPNETCORE_HTTP_PORTS'
              value: '8080'
            }
            {
              name: 'Deployment__Backend'
              value: 'Filesystem'
            }
            {
              name: 'Deployment__DataRoot'
              value: '/data'
            }
            {
              name: 'Deployment__Orleans__Clustering'
              value: 'Localhost'
            }
            {
              name: 'Storage__Name'
              value: 'content'
            }
            {
              name: 'Storage__SourceType'
              value: 'FileSystem'
            }
            {
              name: 'Storage__BasePath'
              value: '/data/content'
            }
            {
              name: 'Graph__Storage__Type'
              value: 'PostgreSql'
            }
            {
              name: 'Graph__Storage__BasePath'
              value: '/data/graph'
            }
            {
              name: 'Mcp__BaseUrl'
              value: 'https://memex-portal.${memex_aca_outputs_azure_container_apps_environment_default_domain}'
            }
          ]
          volumeMounts: [
            {
              volumeName: 'v0'
              mountPath: '/data'
            }
            {
              volumeName: 'v1'
              mountPath: '/mnt/users'
            }
          ]
        }
      ]
      scale: {
        minReplicas: 1
      }
      volumes: [
        {
          name: 'v0'
          storageType: 'AzureFile'
          storageName: memex_aca_outputs_volumes_memex_portal_0
        }
        {
          name: 'v1'
          storageType: 'AzureFile'
          storageName: memex_aca_outputs_volumes_memex_portal_1
        }
      ]
    }
  }
}