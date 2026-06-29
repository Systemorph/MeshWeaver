// ---------------------------------------------------------------------------
// aks.bicep — PRIVATE AKS cluster for the Memex portal.
//
// Key properties for this sample:
//   - apiServerAccessProfile.enablePrivateCluster = true
//       The API server gets a PRIVATE IP only; there is no public control-plane
//       endpoint. `kubectl` therefore only works from inside the VNet — that's
//       why we ship the P2S VPN Gateway (vpn.bicep) and link the private DNS
//       zone (network.bicep).
//   - privateDNSZone = the region-specific zone we created, passed in by id so
//       AKS writes its API server A record there (BYO private DNS zone mode).
//   - userAssignedIdentity for the control plane; the auto-created kubelet
//       identity gets AcrPull on the ACR so nodes can pull private images.
//   - Azure CNI overlay networking inside the aks-nodes subnet.
//   - Workload Identity + OIDC issuer enabled so the pgBackRest sidecar (and any
//       future pod) can use a federated identity to reach Azure Blob without
//       storing account keys (see README "PITR").
//   - Azure Files CSI driver enabled for ReadWriteMany PVCs (azurefile-csi
//       storage class), required for HA portal replicas sharing /mnt/users.
// ---------------------------------------------------------------------------

@description('Azure region for the cluster.')
param location string

@description('AKS cluster name.')
param clusterName string

@description('DNS prefix for the cluster.')
param dnsPrefix string = clusterName

@description('Kubernetes version. Leave empty to use the AKS default for the region.')
param kubernetesVersion string = ''

@description('Subnet resource id for the system node pool (the aks-nodes subnet).')
param aksSubnetId string

@description('Resource id of the BYO private DNS zone (privatelink.<region>.azmk8s.io).')
param privateDnsZoneId string

@description('Resource id of the ACR to grant AcrPull to the kubelet identity. Empty = a shared/cross-RG ACR is used; the AcrPull grant is then done out-of-band (this RG-scoped module cannot author a role assignment in another RG).')
param acrId string = ''

@description('System node pool VM size.')
param systemNodeVmSize string = 'Standard_D4s_v5'

@description('System node pool node count (per-zone if availabilityZones is set).')
@minValue(1)
@maxValue(100)
param systemNodeCount int = 3

@description('Availability zones for the node pool. Empty = no zonal spread.')
param availabilityZones array = [
  '1'
  '2'
  '3'
]

@description('Enable the AKS-managed cluster autoscaler on the system pool.')
param enableAutoScaling bool = true

@description('Autoscaler minimum node count.')
param minNodeCount int = 3

@description('Autoscaler maximum node count.')
param maxNodeCount int = 6

@description('Tags applied to the cluster.')
param tags object = {}

// Control-plane user-assigned identity. Using a UAMI (rather than system-
// assigned) makes the private-DNS-zone role grant deterministic and reusable.
resource aksIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2024-11-30' = {
  name: '${clusterName}-cp-mi'
  location: location
  tags: tags
}

// "Private DNS Zone Contributor" — the control-plane identity must be able to
// write the API server A record into the BYO private DNS zone.
var privateDnsZoneContributorRoleId = 'b12aa53e-6015-4669-85d0-8515ebb3ae7f'

resource dnsZoneRoleAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(privateDnsZoneId, aksIdentity.id, privateDnsZoneContributorRoleId)
  scope: privateDnsZone
  properties: {
    principalId: aksIdentity.properties.principalId
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', privateDnsZoneContributorRoleId)
    principalType: 'ServicePrincipal'
  }
}

// "Network Contributor" on the VNET (not just the node subnet). AKS must manage
// NICs/routes in the subnet AND create the private-DNS-zone -> VNet link during
// reconcile, and that link requires Microsoft.Network/virtualNetworks/join/action
// at the VNET scope. A subnet-scoped grant only covers subnet-level join, so the
// private-DNS reconcile fails with LinkedAuthorizationFailed. (Intermittent — it
// depends on RBAC propagation timing: westeurope happened to pass, swedencentral
// failed. VNet scope is the correct, deterministic fix.)
var networkContributorRoleId = '4d97b98b-1d4f-4787-a291-c67834d212e7'

resource aksVnetRoleAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(aksVnet.id, aksIdentity.id, networkContributorRoleId)
  scope: aksVnet
  properties: {
    principalId: aksIdentity.properties.principalId
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', networkContributorRoleId)
    principalType: 'ServicePrincipal'
  }
}

// Existing references so role assignments can target them by scope.
resource privateDnsZone 'Microsoft.Network/privateDnsZones@2020-06-01' existing = {
  name: last(split(privateDnsZoneId, '/'))
}

// The subnet's parent vnet name and the subnet name are derived from the id.
resource aksVnet 'Microsoft.Network/virtualNetworks@2024-05-01' existing = {
  name: split(aksSubnetId, '/')[8]
}

resource aks 'Microsoft.ContainerService/managedClusters@2024-09-01' = {
  name: clusterName
  location: location
  tags: tags
  sku: {
    name: 'Base'
    tier: 'Standard' // Standard tier = SLA-backed control plane; use for prod.
  }
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${aksIdentity.id}': {}
    }
  }
  properties: {
    dnsPrefix: dnsPrefix
    kubernetesVersion: empty(kubernetesVersion) ? null : kubernetesVersion
    enableRBAC: true
    disableLocalAccounts: false // keep admin kubeconfig usable over the VPN
    // --- PRIVATE CLUSTER: the whole point of this sample -------------------
    apiServerAccessProfile: {
      enablePrivateCluster: true
      privateDNSZone: privateDnsZoneId
      enablePrivateClusterPublicFQDN: false
    }
    // --- Networking --------------------------------------------------------
    networkProfile: {
      networkPlugin: 'azure'
      networkPluginMode: 'overlay'
      networkPolicy: 'cilium'
      networkDataplane: 'cilium'
      loadBalancerSku: 'standard'
      outboundType: 'loadBalancer'
      serviceCidr: '10.43.0.0/16'
      dnsServiceIP: '10.43.0.10'
      podCidr: '10.244.0.0/16'
    }
    // --- Workload Identity (for pgBackRest -> Blob, keyless) ---------------
    oidcIssuerProfile: {
      enabled: true
    }
    securityProfile: {
      workloadIdentity: {
        enabled: true
      }
    }
    // --- Add-ons -----------------------------------------------------------
    addonProfiles: {
      azureKeyvaultSecretsProvider: {
        enabled: true
        config: {
          enableSecretRotation: 'true'
        }
      }
      // NOTE: the Azure Files / Disk CSI drivers are enabled via
      // storageProfile below (the modern location); the azurepolicy and
      // ingress add-ons are intentionally left to the operator (see README:
      // AGIC vs ingress-nginx).
    }
    storageProfile: {
      fileCSIDriver: {
        enabled: true // azurefile-csi storage class for ReadWriteMany PVCs
      }
      diskCSIDriver: {
        enabled: true // managed-csi storage class for the Postgres PVC
      }
      snapshotController: {
        enabled: true
      }
    }
    agentPoolProfiles: [
      {
        name: 'system'
        mode: 'System'
        osType: 'Linux'
        osSKU: 'AzureLinux'
        vmSize: systemNodeVmSize
        count: systemNodeCount
        enableAutoScaling: enableAutoScaling
        minCount: enableAutoScaling ? minNodeCount : null
        maxCount: enableAutoScaling ? maxNodeCount : null
        vnetSubnetID: aksSubnetId
        availabilityZones: empty(availabilityZones) ? null : availabilityZones
        type: 'VirtualMachineScaleSets'
        maxPods: 60
      }
    ]
  }
  dependsOn: [
    dnsZoneRoleAssignment
    aksVnetRoleAssignment
  ]
}

// Grant the auto-created kubelet identity AcrPull on the ACR so nodes can pull
// private images imported into the registry.
var acrPullRoleId = '7f951dda-4ed3-4680-a7ca-43fe172d538d'

resource acr 'Microsoft.ContainerRegistry/registries@2025-04-01' existing = if (!empty(acrId)) {
  name: last(split(acrId, '/'))
}

resource acrPullRoleAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (!empty(acrId)) {
  name: guid(acrId, aks.id, acrPullRoleId)
  scope: acr
  properties: {
    principalId: aks.properties.identityProfile.kubeletidentity.objectId
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', acrPullRoleId)
    principalType: 'ServicePrincipal'
  }
}

output clusterName string = aks.name
output clusterId string = aks.id
output controlPlaneIdentityId string = aksIdentity.id
output kubeletIdentityObjectId string = aks.properties.identityProfile.kubeletidentity.objectId
output oidcIssuerUrl string = aks.properties.oidcIssuerProfile.issuerURL
output nodeResourceGroup string = aks.properties.nodeResourceGroup
// The private API server FQDN is read back at deploy time from the cluster's
// fqdn property (the private endpoint variant). `az aks get-credentials`
// discovers it automatically, so this is informational only.
output apiServerPrivateFqdn string = aks.properties.privateFQDN
