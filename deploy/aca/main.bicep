targetScope = 'subscription'

param resourceGroupName string

param location string

param principalId string

resource rg 'Microsoft.Resources/resourceGroups@2023-07-01' = {
  name: resourceGroupName
  location: location
}

module memex_aca_acr 'memex-aca-acr/memex-aca-acr.bicep' = {
  name: 'memex-aca-acr'
  scope: rg
  params: {
    location: location
  }
}

module memex_aca 'memex-aca/memex-aca.bicep' = {
  name: 'memex-aca'
  scope: rg
  params: {
    location: location
    memex_aca_acr_outputs_name: memex_aca_acr.outputs.name
    userPrincipalId: principalId
  }
}

output memex_aca_AZURE_CONTAINER_APPS_ENVIRONMENT_DEFAULT_DOMAIN string = memex_aca.outputs.AZURE_CONTAINER_APPS_ENVIRONMENT_DEFAULT_DOMAIN

output memex_aca_AZURE_CONTAINER_APPS_ENVIRONMENT_ID string = memex_aca.outputs.AZURE_CONTAINER_APPS_ENVIRONMENT_ID

output memex_aca_volumes_memex_postgres_0 string = memex_aca.outputs.volumes_memex_postgres_0

output memex_aca_volumes_memex_portal_0 string = memex_aca.outputs.volumes_memex_portal_0

output memex_aca_volumes_memex_portal_1 string = memex_aca.outputs.volumes_memex_portal_1