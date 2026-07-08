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

// --- Portal Workload Identity (self-updater ACR polling) -------------------
@description('Provision the portal user-assigned managed identity + federated credentials (Workload Identity) so the in-pod self-updater can authenticate to ACR (AcrPull) and list image tags keyless. Mirrors the pgBackRest WI wiring.')
param deployPortalIdentity bool = true

@description('Name of the portal UAMI. Empty = "<namePrefix>-portal-mi".')
param portalIdentityName string = ''

@description('Kubernetes namespaces that run the portal. One federated credential (subject system:serviceaccount:<ns>:memex-portal-sa) is created per namespace. Keep in sync with the deployed environments (memex, atioz, memex-cloud).')
param portalNamespaces array = [
  'memex'
  'atioz'
  'memex-cloud'
]

@description('Author AcrPull for the portal UAMI on the SHARED (cross-RG) registry as IaC. Requires the deploying principal to have User Access Administrator/Owner on the shared ACR resource group. Default false = grant it out-of-band (az role assignment create), matching how the kubelet AcrPull is granted. Ignored when a per-deployment ACR is created (that grant is authored in-bicep).')
param grantSharedAcrPull bool = false

@description('Resource group of the SHARED ACR (used only when sharedAcrLoginServer is set AND grantSharedAcrPull=true). Defaults to the shared registry RG.')
param sharedAcrResourceGroup string = 'meshweaver-shared'

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

@description('Store PG managed backups geo-redundantly (paired-region copy for regional DR). IMMUTABLE after server creation — see DatabaseBackups.md for enabling on an existing server. On by default.')
param postgresGeoRedundantBackup bool = true

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
var effectivePortalIdentityName = empty(portalIdentityName) ? '${namePrefix}-portal-mi' : portalIdentityName
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

// Portal Workload Identity: UAMI + one federated credential per portal namespace
// (subject system:serviceaccount:<ns>:memex-portal-sa) for the in-pod self-updater.
// Same-RG ACR -> AcrPull authored in the module; shared cross-RG ACR -> see
// portalSharedAcrPull below (opt-in) or the out-of-band grant in the README.
module portalIdentity 'modules/portal-identity.bicep' = if (deployPortalIdentity) {
  name: 'portalIdentity'
  scope: rg
  params: {
    location: location
    identityName: effectivePortalIdentityName
    oidcIssuerUrl: aks.outputs.oidcIssuerUrl
    namespaces: portalNamespaces
    acrId: effectiveAcrId // empty for the shared cross-RG registry (grant is out-of-band / portalSharedAcrPull)
    tags: tags
  }
}

// Opt-in IaC AcrPull for the portal UAMI on the SHARED (cross-RG) registry. Deployed
// to the registry's own RG (so a cross-RG role assignment is allowed). Off by default
// (the deployer often lacks UAA on meshweaver-shared) — then grant out-of-band.
module portalSharedAcrPull 'modules/acr-role-assignment.bicep' = if (deployPortalIdentity && useSharedAcr && grantSharedAcrPull) {
  name: 'portalSharedAcrPull'
  scope: resourceGroup(sharedAcrResourceGroup)
  params: {
    acrName: split(sharedAcrLoginServer, '.')[0]
    principalId: portalIdentity!.outputs.portalIdentityPrincipalId
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
    geoRedundantBackup: postgresGeoRedundantBackup
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

// --- Portal Workload Identity (self-updater) -------------------------------
// portalIdentityClientId -> selfUpdate.azureClientId (Helm) for EVERY portal namespace.
// portalIdentityPrincipalId -> the objectId for the out-of-band / cross-RG AcrPull grant.
output portalIdentityName string = portalIdentity.?outputs.portalIdentityName ?? ''
output portalIdentityClientId string = portalIdentity.?outputs.portalIdentityClientId ?? ''
output portalIdentityPrincipalId string = portalIdentity.?outputs.portalIdentityPrincipalId ?? ''

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
