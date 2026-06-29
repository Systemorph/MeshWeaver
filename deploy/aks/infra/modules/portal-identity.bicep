// ---------------------------------------------------------------------------
// portal-identity.bicep — Azure Workload Identity for the Memex PORTAL pod, so
//   the in-pod self-updater (SelfUpdateHostedService -> AcrTagLister) can
//   authenticate to ACR and list image tags KEYLESS.
//
// Mirrors the pgBackRest workload-identity wiring in storage.bicep, generalised
// to the portal namespaces that share this one cluster (memex, atioz,
// memex-cloud — see .github/workflows/main-cd.yml). ONE user-assigned managed
// identity, federated to
//   system:serviceaccount:<ns>:memex-portal-sa
// for EVERY namespace that runs the portal, and granted AcrPull on the registry.
// All envs run the SAME service account (memex-portal-sa) and pull from the SAME
// shared registry, so one shared UAMI (one AcrPull grant, one client id wired
// into selfUpdate.azureClientId everywhere) is the right scoping — exactly how
// pgBackRest uses one identity for its namespace.
//
// AUTH FLOW (see memex/Memex.Portal.Shared/SelfUpdate/AcrTagLister.cs): the pod
// receives a projected federated token because the azure.workload.identity
// webhook acts on the memex-portal-sa SA annotation
// (azure.workload.identity/client-id = this UAMI's clientId) + the pod label
// (azure.workload.identity/use: "true") — both emitted by the Helm chart when
// selfUpdate.azureClientId is set. ManagedIdentityCredential(AZURE_CLIENT_ID)
// then exchanges that token for an AAD token and, in turn, for an ACR token.
// AcrPull carries the repository:*:metadata_read scope the tag-list call needs.
//
// ACR SCOPING — two cases, mirroring the kubelet AcrPull in aks.bicep:
//   * Same-RG ACR (acrId set — a per-deployment registry from acr.bicep): this
//     module authors the AcrPull role assignment directly (same RG, clean).
//   * Shared cross-RG ACR (the default meshweaver.azurecr.io in RG
//     meshweaver-shared): an RG-scoped module CANNOT author a role assignment in
//     another RG, so acrId is left empty here and the grant is done either by the
//     subscription-scoped acr-role-assignment.bicep module (opt-in IaC) or
//     OUT-OF-BAND via `az role assignment create` — exactly as the cluster
//     kubelet's AcrPull is granted (see DEPLOY-RUNBOOK.md / README.md).
// ---------------------------------------------------------------------------

@description('Azure region for the managed identity.')
param location string

@description('Name of the user-assigned managed identity for the portal.')
param identityName string

@description('OIDC issuer URL of the AKS cluster (from aks.bicep output). Same issuer for every namespace on the cluster.')
param oidcIssuerUrl string

@description('Kubernetes namespaces that run the portal. One federated credential is created per namespace, each federating the memex-portal-sa service account in that namespace.')
param namespaces array = [
  'memex'
  'atioz'
  'memex-cloud'
]

@description('Name of the portal ServiceAccount the federated credentials trust. MUST match the Helm chart (deploy/helm/templates/memex-portal/serviceaccount.yaml). Do not change unless the chart changes.')
param serviceAccountName string = 'memex-portal-sa'

@description('Resource id of a SAME-RG ACR to grant AcrPull to. Empty (the default for a shared cross-RG registry like meshweaver.azurecr.io) = no in-bicep grant; do it via acr-role-assignment.bicep or out-of-band (see README).')
param acrId string = ''

@description('Tags applied to the identity.')
param tags object = {}

resource portalIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2024-11-30' = {
  name: identityName
  location: location
  tags: tags
}

// One federated credential per namespace: subject system:serviceaccount:<ns>:<sa>.
// The issuer is the single cluster OIDC issuer; the subject's namespace MUST match
// the namespace the portal Deployment + memex-portal-sa actually run in.
resource portalFederatedCredentials 'Microsoft.ManagedIdentity/userAssignedIdentities/federatedIdentityCredentials@2024-11-30' = [
  for ns in namespaces: {
    parent: portalIdentity
    name: 'memex-portal-${ns}'
    properties: {
      issuer: oidcIssuerUrl
      subject: 'system:serviceaccount:${ns}:${serviceAccountName}'
      audiences: [
        'api://AzureADTokenExchange'
      ]
    }
  }
]

// "AcrPull" (includes repository metadata_read) — authored here only for the
// SAME-RG ACR case. For the shared cross-RG registry acrId is empty and this is
// skipped (see header).
var acrPullRoleId = '7f951dda-4ed3-4680-a7ca-43fe172d538d'

resource acr 'Microsoft.ContainerRegistry/registries@2025-04-01' existing = if (!empty(acrId)) {
  name: last(split(acrId, '/'))
}

resource portalAcrPullRoleAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (!empty(acrId)) {
  name: guid(acrId, portalIdentity.id, acrPullRoleId)
  scope: acr
  properties: {
    principalId: portalIdentity.properties.principalId
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', acrPullRoleId)
    principalType: 'ServicePrincipal'
  }
}

output portalIdentityName string = portalIdentity.name
output portalIdentityId string = portalIdentity.id
// Wire this into selfUpdate.azureClientId (Helm) for EVERY portal namespace.
output portalIdentityClientId string = portalIdentity.properties.clientId
// Use this principalId (objectId) for the out-of-band / cross-RG AcrPull grant.
output portalIdentityPrincipalId string = portalIdentity.properties.principalId
