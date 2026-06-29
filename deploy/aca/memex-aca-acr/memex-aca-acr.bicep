@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

resource memex_aca_acr 'Microsoft.ContainerRegistry/registries@2025-04-01' = {
  name: take('memexacaacr${uniqueString(resourceGroup().id)}', 50)
  location: location
  sku: {
    name: 'Basic'
  }
  tags: {
    'aspire-resource-name': 'memex-aca-acr'
  }
}

output name string = memex_aca_acr.name

output loginServer string = memex_aca_acr.properties.loginServer

output id string = memex_aca_acr.id