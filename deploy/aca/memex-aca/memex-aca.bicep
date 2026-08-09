@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

param userPrincipalId string = ''

param tags object = { }

param memex_aca_acr_outputs_name string

resource memex_aca_mi 'Microsoft.ManagedIdentity/userAssignedIdentities@2024-11-30' = {
  name: take('memex_aca_mi-${uniqueString(resourceGroup().id)}', 128)
  location: location
  tags: tags
}

resource memex_aca_acr 'Microsoft.ContainerRegistry/registries@2025-04-01' existing = {
  name: memex_aca_acr_outputs_name
}

resource memex_aca_acr_memex_aca_mi_AcrPull 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(memex_aca_acr.id, memex_aca_mi.id, subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '7f951dda-4ed3-4680-a7ca-43fe172d538d'))
  properties: {
    principalId: memex_aca_mi.properties.principalId
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '7f951dda-4ed3-4680-a7ca-43fe172d538d')
    principalType: 'ServicePrincipal'
  }
  scope: memex_aca_acr
}

resource memex_aca_law 'Microsoft.OperationalInsights/workspaces@2025-02-01' = {
  name: take('memexacalaw-${uniqueString(resourceGroup().id)}', 63)
  location: location
  properties: {
    sku: {
      name: 'PerGB2018'
    }
  }
  tags: tags
}

resource memex_aca 'Microsoft.App/managedEnvironments@2025-07-01' = {
  name: take('memexaca${uniqueString(resourceGroup().id)}', 24)
  location: location
  properties: {
    appLogsConfiguration: {
      destination: 'log-analytics'
      logAnalyticsConfiguration: {
        customerId: memex_aca_law.properties.customerId
        sharedKey: memex_aca_law.listKeys().primarySharedKey
      }
    }
    workloadProfiles: [
      {
        name: 'consumption'
        workloadProfileType: 'Consumption'
      }
    ]
  }
  tags: tags
}

resource aspireDashboard 'Microsoft.App/managedEnvironments/dotNetComponents@2025-10-02-preview' = {
  name: 'aspire-dashboard'
  properties: {
    componentType: 'AspireDashboard'
  }
  parent: memex_aca
}

resource memex_aca_storageVolume 'Microsoft.Storage/storageAccounts@2024-01-01' = {
  name: take('memexacastoragevolume${uniqueString(resourceGroup().id)}', 24)
  kind: 'StorageV2'
  location: location
  sku: {
    name: 'Standard_LRS'
  }
  properties: {
    largeFileSharesState: 'Enabled'
    minimumTlsVersion: 'TLS1_2'
  }
  tags: tags
}

resource storageVolumeFileService 'Microsoft.Storage/storageAccounts/fileServices@2024-01-01' = {
  name: 'default'
  parent: memex_aca_storageVolume
}

resource shares_volumes_memex_postgres_0 'Microsoft.Storage/storageAccounts/fileServices/shares@2024-01-01' = {
  name: take('sharesvolumesmemexpostgres0-${uniqueString(resourceGroup().id)}', 63)
  properties: {
    enabledProtocols: 'SMB'
    shareQuota: 1024
  }
  parent: storageVolumeFileService
}

resource managedStorage_volumes_memex_postgres_0 'Microsoft.App/managedEnvironments/storages@2025-07-01' = {
  name: take('managedstoragevolumesmemexpostgres${uniqueString(resourceGroup().id)}', 24)
  properties: {
    azureFile: {
      accountName: memex_aca_storageVolume.name
      accountKey: memex_aca_storageVolume.listKeys().keys[0].value
      accessMode: 'ReadWrite'
      shareName: shares_volumes_memex_postgres_0.name
    }
  }
  parent: memex_aca
}

resource shares_volumes_memex_portal_0 'Microsoft.Storage/storageAccounts/fileServices/shares@2024-01-01' = {
  name: take('sharesvolumesmemexportal0-${uniqueString(resourceGroup().id)}', 63)
  properties: {
    enabledProtocols: 'SMB'
    shareQuota: 1024
  }
  parent: storageVolumeFileService
}

resource managedStorage_volumes_memex_portal_0 'Microsoft.App/managedEnvironments/storages@2025-07-01' = {
  name: take('managedstoragevolumesmemexportal${uniqueString(resourceGroup().id)}', 24)
  properties: {
    azureFile: {
      accountName: memex_aca_storageVolume.name
      accountKey: memex_aca_storageVolume.listKeys().keys[0].value
      accessMode: 'ReadWrite'
      shareName: shares_volumes_memex_portal_0.name
    }
  }
  parent: memex_aca
}

resource shares_volumes_memex_portal_1 'Microsoft.Storage/storageAccounts/fileServices/shares@2024-01-01' = {
  name: take('sharesvolumesmemexportal1-${uniqueString(resourceGroup().id)}', 63)
  properties: {
    enabledProtocols: 'SMB'
    shareQuota: 1024
  }
  parent: storageVolumeFileService
}

resource managedStorage_volumes_memex_portal_1 'Microsoft.App/managedEnvironments/storages@2025-07-01' = {
  name: take('managedstoragevolumesmemexportal${uniqueString(resourceGroup().id)}', 24)
  properties: {
    azureFile: {
      accountName: memex_aca_storageVolume.name
      accountKey: memex_aca_storageVolume.listKeys().keys[0].value
      accessMode: 'ReadWrite'
      shareName: shares_volumes_memex_portal_1.name
    }
  }
  parent: memex_aca
}

output volumes_memex_postgres_0 string = managedStorage_volumes_memex_postgres_0.name

output volumes_memex_portal_0 string = managedStorage_volumes_memex_portal_0.name

output volumes_memex_portal_1 string = managedStorage_volumes_memex_portal_1.name

output AZURE_LOG_ANALYTICS_WORKSPACE_NAME string = memex_aca_law.name

output AZURE_LOG_ANALYTICS_WORKSPACE_ID string = memex_aca_law.id

output AZURE_CONTAINER_REGISTRY_NAME string = memex_aca_acr.name

output AZURE_CONTAINER_REGISTRY_ENDPOINT string = memex_aca_acr.properties.loginServer

output AZURE_CONTAINER_REGISTRY_MANAGED_IDENTITY_ID string = memex_aca_mi.id

output AZURE_CONTAINER_APPS_ENVIRONMENT_NAME string = memex_aca.name

output AZURE_CONTAINER_APPS_ENVIRONMENT_ID string = memex_aca.id

output AZURE_CONTAINER_APPS_ENVIRONMENT_DEFAULT_DOMAIN string = memex_aca.properties.defaultDomain