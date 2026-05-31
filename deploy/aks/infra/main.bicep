// ===========================================================================
// main.bicep — Top-level infrastructure for the MeshWeaver Memex AKS SAMPLE.
//
// Scope: subscription. Creates the resource group, then wires:
//   network.bicep  -> VNet + subnets + private DNS zone (privatelink.<rgn>.azmk8s.io)
//   acr.bicep      -> Azure Container Registry (private image cache/mirror)
//   aks.bicep      -> PRIVATE AKS cluster (private API server) + AcrPull grant
//   vpn.bicep      -> P2S VPN Gateway so kubectl reaches the private API server
//   storage.bicep  -> Blob storage + Workload Identity for pgBackRest PITR
//
// Deploy:
//   az deployment sub create \
//     --location <region> \
//     --template-file main.bicep \
//     --parameters @main.parameters.json
//
// This provisions INFRA ONLY. The portal itself is installed afterwards with
// Helm using ../values.aks.yaml against the existing ../helm chart (see README).
//
// NOTE on Aspire: this sample is the operator-facing IaC. The repo's Aspire
// model (deploy/aspire/Memex.Deploy.AppHost) generated the ../helm chart and the
// ../aca bicep; it does not emit a private-AKS + VPN + pgBackRest topology.
// See ../README.md "Generating this from Aspire" for how this sample relates to
// the Aspire publish output and how to keep them in sync.
// ===========================================================================

targetScope = 'subscription'

@description('Resource group to create / deploy into.')
param resourceGroupName string = 'memex-aks-rg'

@description('Azure region for all resources.')
param location string = 'westeurope'

@description('Short name prefix used across resources (lowercase, <= 12 chars).')
@maxLength(12)
param namePrefix string = 'memexaks'

// --- ACR -------------------------------------------------------------------
@description('Login server of an EXISTING shared ACR (e.g. meshweaver.azurecr.io). When set, NO per-RG ACR is created — images are pulled from this shared registry, and the cluster kubelet must be granted AcrPull on it out-of-band (cross-RG, see README). Leave empty to create a dedicated per-RG ACR.')
param sharedAcrLoginServer string = 'meshweaver.azurecr.io'

@description('ACR name (globally unique). Only used when sharedAcrLoginServer is empty. Defaults to a hashed name.')
param acrName string = ''

@description('ACR SKU. Only used when sharedAcrLoginServer is empty.')
param acrSku string = 'Premium'

// --- AKS -------------------------------------------------------------------
@description('Kubernetes version. Empty = region default.')
param kubernetesVersion string = ''

@description('System node pool VM size.')
param systemNodeVmSize string = 'Standard_D4s_v5'

@description('Initial / desired node count.')
param systemNodeCount int = 3

@description('Enable cluster autoscaler.')
param enableAutoScaling bool = true

@description('Autoscaler min nodes.')
param minNodeCount int = 3

@description('Autoscaler max nodes.')
param maxNodeCount int = 6

@description('Availability zones for nodes.')
param availabilityZones array = [
  '1'
  '2'
  '3'
]

// --- Networking ------------------------------------------------------------
@description('VNet address space.')
param vnetAddressSpace string = '10.42.0.0/16'

// --- VPN -------------------------------------------------------------------
@description('Deploy the P2S VPN Gateway (set false to use az aks command invoke / Bastion instead).')
param deployVpnGateway bool = true

@description('P2S client address pool (must not overlap the VNet).')
param vpnClientAddressPool string = '172.16.201.0/24'

@description('Base64 public cert data of the P2S root cert (single line, no PEM headers). Empty = add later.')
param vpnClientRootCertData string = ''

@description('VPN Gateway SKU. Must be a zone-redundant *AZ SKU — Azure retired the non-AZ VpnGw1-5 SKUs (NonAzSkusNotAllowedForVPNGateway).')
param gatewaySku string = 'VpnGw1AZ'

// --- Backup storage --------------------------------------------------------
@description('Deploy the pgBackRest blob storage + workload identity (self-managed PITR). Set false if using Azure DB Flexible Server.')
param deployBackupStorage bool = true

@description('Storage account name for pgBackRest (globally unique, 3-24 lowercase). Empty = hashed name.')
param backupStorageAccountName string = ''

@description('Kubernetes namespace + service account the pgBackRest pods run as.')
param pgBackRestServiceAccountSubject string = 'system:serviceaccount:memex:pgbackrest-sa'

// --- Content / log Azure Files shares --------------------------------------
@description('Provision the dedicated Azure Files account with named shares (content, attachments, users, data, otel-logs) for STATIC PV binding. Dynamic azurefile provisioning is the default and does not require this.')
param deployContentFileShares bool = true

@description('Azure Files account name for content + observability shares (globally unique, 3-24 lowercase). Empty = hashed name.')
param contentFilesAccountName string = ''

// --- Private PostgreSQL Flexible Server ------------------------------------
@description('Provision a PRIVATE (VNet-injected) Azure Database for PostgreSQL Flexible Server with pgvector. When true, point the portal at its FQDN and set deployBackupStorage=false (managed PITR replaces pgBackRest).')
param deployPostgresFlexible bool = true

@description('Flexible Server name (globally unique). Empty = "<namePrefix>-pg".')
param postgresServerName string = ''

@description('PostgreSQL administrator password. REQUIRED when deployPostgresFlexible=true. Pass at deploy time (--parameters or env); never commit it.')
@secure()
param postgresAdminPassword string = ''

@description('Flexible Server compute SKU (Standard_D2ds_v5 = 2 vCPU/8 GiB; Standard_D4ds_v5 = 4 vCPU/16 GiB).')
param postgresSkuName string = 'Standard_D2ds_v5'

@description('Flexible Server zone-redundant HA (standby in another AZ). Off by default — not every region allows it (e.g. westeurope returns HADisabledForRegion); enable only where supported.')
param postgresHighAvailability bool = false

@description('Tags applied to all resources.')
param tags object = {
  project: 'meshweaver-memex'
  sample: 'aks'
}

// Deterministic unique names where the caller didn't supply one.
var effectiveAcrName = empty(acrName) ? take('${namePrefix}acr${uniqueString(subscription().id, resourceGroupName)}', 50) : acrName
var effectiveBackupSa = empty(backupStorageAccountName) ? take('${namePrefix}bkp${uniqueString(subscription().id, resourceGroupName)}', 24) : backupStorageAccountName
var effectiveContentFilesSa = empty(contentFilesAccountName) ? take('${namePrefix}files${uniqueString(subscription().id, resourceGroupName)}', 24) : contentFilesAccountName
var effectivePostgresServer = empty(postgresServerName) ? '${namePrefix}-pg' : postgresServerName
var clusterName = '${namePrefix}-cluster'

// Shared-ACR axis: when sharedAcrLoginServer is set, no per-RG ACR is created and the
// kubelet's AcrPull on the shared registry is granted out-of-band (cross-RG — the role
// assignment can't be authored from this RG-scoped module). effectiveAcrId is then empty,
// which makes aks.bicep skip its (same-RG) AcrPull grant.
var useSharedAcr = !empty(sharedAcrLoginServer)
var effectiveAcrId = useSharedAcr ? '' : acr!.outputs.acrId
var effectiveAcrLoginServer = useSharedAcr ? sharedAcrLoginServer : acr!.outputs.acrLoginServer

resource rg 'Microsoft.Resources/resourceGroups@2024-03-01' = {
  name: resourceGroupName
  location: location
  tags: tags
}

module network 'modules/network.bicep' = {
  name: 'network'
  scope: rg
  params: {
    location: location
    namePrefix: namePrefix
    vnetAddressSpace: vnetAddressSpace
    tags: tags
  }
}

module acr 'modules/acr.bicep' = if (!useSharedAcr) {
  name: 'acr'
  scope: rg
  params: {
    location: location
    acrName: effectiveAcrName
    acrSku: acrSku
    tags: tags
  }
}

module aks 'modules/aks.bicep' = {
  name: 'aks'
  scope: rg
  params: {
    location: location
    clusterName: clusterName
    kubernetesVersion: kubernetesVersion
    aksSubnetId: network.outputs.aksSubnetId
    privateDnsZoneId: network.outputs.privateDnsZoneId
    acrId: effectiveAcrId
    systemNodeVmSize: systemNodeVmSize
    systemNodeCount: systemNodeCount
    enableAutoScaling: enableAutoScaling
    minNodeCount: minNodeCount
    maxNodeCount: maxNodeCount
    availabilityZones: availabilityZones
    tags: tags
  }
}

module vpn 'modules/vpn.bicep' = if (deployVpnGateway) {
  name: 'vpn'
  scope: rg
  params: {
    location: location
    namePrefix: namePrefix
    gatewaySubnetId: network.outputs.gatewaySubnetId
    vpnClientAddressPool: vpnClientAddressPool
    vpnClientRootCertData: vpnClientRootCertData
    gatewaySku: gatewaySku
    tags: tags
  }
}

module storage 'modules/storage.bicep' = if (deployBackupStorage) {
  name: 'backupStorage'
  scope: rg
  params: {
    location: location
    storageAccountName: effectiveBackupSa
    oidcIssuerUrl: aks.outputs.oidcIssuerUrl
    serviceAccountSubject: pgBackRestServiceAccountSubject
    tags: tags
  }
}

module contentFiles 'modules/files.bicep' = if (deployContentFileShares) {
  name: 'contentFiles'
  scope: rg
  params: {
    location: location
    storageAccountName: effectiveContentFilesSa
    tags: tags
  }
}

module postgres 'modules/postgres.bicep' = if (deployPostgresFlexible) {
  name: 'postgres'
  scope: rg
  params: {
    location: location
    serverName: effectivePostgresServer
    delegatedSubnetId: network.outputs.postgresSubnetId
    privateDnsZoneId: network.outputs.postgresPrivateDnsZoneId
    administratorPassword: postgresAdminPassword
    skuName: postgresSkuName
    highAvailability: postgresHighAvailability
    tags: tags
  }
}

// --- Outputs (consumed by the README's get-credentials / helm steps) -------
output resourceGroupName string = rg.name
output clusterName string = aks.outputs.clusterName
output apiServerPrivateFqdn string = aks.outputs.apiServerPrivateFqdn
output oidcIssuerUrl string = aks.outputs.oidcIssuerUrl
output acrLoginServer string = effectiveAcrLoginServer
output acrName string = useSharedAcr ? split(sharedAcrLoginServer, '.')[0] : acr!.outputs.acrName
output privateDnsZoneName string = network.outputs.privateDnsZoneName
output vpnGatewayName string = vpn.?outputs.vpnGatewayName ?? ''
output backupStorageAccount string = storage.?outputs.storageAccountName ?? ''
output backupBlobEndpoint string = storage.?outputs.blobEndpoint ?? ''
output backupContainerName string = storage.?outputs.backupContainerName ?? ''
output pgBackRestIdentityClientId string = storage.?outputs.backupIdentityClientId ?? ''

// --- Content / log Azure Files (static PV binding) -------------------------
output contentFilesAccount string = contentFiles.?outputs.storageAccountName ?? ''
output contentFilesEndpoint string = contentFiles.?outputs.fileEndpoint ?? ''
output contentShareName string = contentFiles.?outputs.contentShareName ?? ''
output attachmentsShareName string = contentFiles.?outputs.attachmentsShareName ?? ''
output dataShareName string = contentFiles.?outputs.dataShareName ?? ''
output usersShareName string = contentFiles.?outputs.usersShareName ?? ''
output otelLogsShareName string = contentFiles.?outputs.otelLogsShareName ?? ''

// --- Private PostgreSQL Flexible Server ------------------------------------
output postgresServerName string = postgres.?outputs.serverName ?? ''
output postgresFqdn string = postgres.?outputs.fullyQualifiedDomainName ?? ''
output postgresDatabaseName string = postgres.?outputs.databaseName ?? ''
output postgresAdminLogin string = postgres.?outputs.administratorLogin ?? ''
