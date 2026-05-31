// ---------------------------------------------------------------------------
// acr.bicep — Azure Container Registry for the Memex portal images.
//
// Two usage modes (see README "Image strategy"):
//   1. Pull straight from GHCR (ghcr.io/systemorph/...) — then this ACR is
//      optional and used only as a private cache/mirror.
//   2. Import the GHCR images into this ACR (`az acr import ...`) and point the
//      Helm overlay's image.registry at acr.loginServer — recommended for a
//      private cluster with no public egress.
//
// AcrPull is granted to the AKS cluster's kubelet managed identity in aks.bicep
// (the kubelet identity is only known after the cluster exists), so this module
// just emits the registry. anonymousPullEnabled stays false.
// ---------------------------------------------------------------------------

@description('Azure region for the registry.')
param location string

@description('ACR name. Must be globally unique, alphanumeric, 5-50 chars.')
param acrName string

@description('ACR SKU. Premium is required for private endpoints / geo-replication.')
@allowed([
  'Basic'
  'Standard'
  'Premium'
])
param acrSku string = 'Premium'

@description('Tags applied to the registry.')
param tags object = {}

resource acr 'Microsoft.ContainerRegistry/registries@2025-04-01' = {
  name: acrName
  location: location
  tags: tags
  sku: {
    name: acrSku
  }
  properties: {
    adminUserEnabled: false
    anonymousPullEnabled: false
    // Premium-only knobs; harmless defaults on lower SKUs.
    publicNetworkAccess: 'Enabled'
    zoneRedundancy: 'Disabled'
  }
}

output acrId string = acr.id
output acrName string = acr.name
output acrLoginServer string = acr.properties.loginServer
