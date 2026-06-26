// ---------------------------------------------------------------------------
// acr-role-assignment.bicep — grants AcrPull on an EXISTING registry to a
//   principal. Deployed to the REGISTRY'S OWN resource group (the caller sets
//   `scope: resourceGroup(<acr rg>)`), which is what lets the subscription-scoped
//   parent (main.bicep) author a role assignment ACROSS resource groups.
//
// Used as the opt-in IaC alternative to the out-of-band `az role assignment
// create` for the cross-RG shared registry (meshweaver.azurecr.io lives in RG
// meshweaver-shared, separate from memex-aks-rg). It grants the PORTAL UAMI
// AcrPull so the in-pod self-updater can list image tags. The deploying
// principal must have User Access Administrator (or Owner) on the registry's
// resource group for this to succeed — that is why it is opt-in (default off):
// where the deployer lacks that role, grant AcrPull out-of-band instead, exactly
// as the cluster kubelet's AcrPull is granted (DEPLOY-RUNBOOK.md step 2).
// ---------------------------------------------------------------------------

@description('Name of the EXISTING container registry in this resource group (e.g. "meshweaver").')
param acrName string

@description('Principal (objectId) to grant AcrPull to — the portal UAMI principalId.')
param principalId string

@description('Principal type. ServicePrincipal for a managed identity (skips the AAD propagation check).')
param principalType string = 'ServicePrincipal'

// "AcrPull" — includes the repository:*:metadata_read scope the ACR tag-list uses.
var acrPullRoleId = '7f951dda-4ed3-4680-a7ca-43fe172d538d'

resource acr 'Microsoft.ContainerRegistry/registries@2025-04-01' existing = {
  name: acrName
}

resource acrPull 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(acr.id, principalId, acrPullRoleId)
  scope: acr
  properties: {
    principalId: principalId
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', acrPullRoleId)
    principalType: principalType
  }
}

output roleAssignmentId string = acrPull.id
