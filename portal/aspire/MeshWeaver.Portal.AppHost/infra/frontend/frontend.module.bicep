@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

param frontend_containerport string

param meshweaverblobs_outputs_tableendpoint string

param mesh_cluster_id_value string

param mesh_service_id_value string

param meshweaverblobs_outputs_blobendpoint string

param azure_postgres_outputs_connectionstring string

param entraidtenantid_value string

param entraidclientid_value string

param portaladmingroup_value string

param googleanalyticstrackingid_value string = ''

param outputs_azure_container_registry_managed_identity_id string

param outputs_managed_identity_client_id string

param outputs_azure_container_apps_environment_id string

param outputs_azure_container_registry_endpoint string

param frontend_containerimage string

param meshweaverCertificate string
param meshweaverCertificate2 string = ''

param meshweaverDomain string
param meshweaverDomain2 string = ''

resource frontend 'Microsoft.App/containerApps@2024-03-01' = {
  name: 'frontend'
  location: location
  properties: {
    configuration: {
      activeRevisionsMode: 'Single'
      ingress: {
        external: true
        targetPort: frontend_containerport
        transport: 'http'
        stickySessions: {
          affinity: 'sticky'
        }
        customDomains: concat(
              [
                {
                  name: meshweaverDomain
                  bindingType: (meshweaverCertificate != '') ? 'SniEnabled' : 'Disabled'
                  certificateId: (meshweaverCertificate != '') ? '${outputs_azure_container_apps_environment_id}/managedCertificates/${meshweaverCertificate}' : null
                }
              ],
              (meshweaverCertificate2 != '') ? [
                {
                  name: meshweaverDomain2
                  bindingType: 'SniEnabled'
                  certificateId: '${outputs_azure_container_apps_environment_id}/managedCertificates/${meshweaverCertificate2}'
                }
              ] : []
        )      
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
          image: frontend_containerimage
          name: 'frontend'
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
              value: frontend_containerport
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
              name: 'ConnectionStrings__documentation'
              value: meshweaverblobs_outputs_blobendpoint
            }
            {
              name: 'ConnectionStrings__reinsurance'
              value: meshweaverblobs_outputs_blobendpoint
            }
            {
              name: 'ConnectionStrings__meshweaverdb'
              value: '${azure_postgres_outputs_connectionstring};Database=meshweaverdb'
            }
            {
              name: 'EntraId__TenantId'
              value: entraidtenantid_value
            }
            {
              name: 'EntraId__ClientId'
              value: entraidclientid_value
            }
            {
              name: 'EntraId__RoleMappings__PortalAdmin'
              value: portaladmingroup_value
            }
            {
              name: 'GoogleAnalyticsTrackingId'
              value: googleanalyticstrackingid_value
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