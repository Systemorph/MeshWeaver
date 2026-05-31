// ---------------------------------------------------------------------------
// storage.bicep — Azure Blob storage for pgBackRest PITR backups of the
//                 self-managed Postgres container, plus a keyless
//                 (Workload Identity / federated) path for the backup pods.
//
// pgBackRest writes full + differential backups AND archived WAL segments to a
// blob container. With WAL archiving on, `pgbackrest restore --type=time` can
// roll the database forward to any second between the last full backup and the
// last archived WAL — that's Point-In-Time Recovery.
//
// Auth model: AKS Workload Identity. We create a user-assigned managed identity,
// grant it "Storage Blob Data Contributor" on the storage account, and federate
// it with the pgBackRest Kubernetes service account (subject is supplied by the
// caller as serviceAccountSubject, e.g.
//   system:serviceaccount:memex:pgbackrest-sa
// ). The pod then authenticates to Blob with no account key on disk.
//
// pgBackRest can also use a shared key / SAS token (set repo1-azure-key in the
// pgbackrest secret instead) — see README if you prefer keys over Workload
// Identity. Account-key auth is left enabled here so the simpler path works too.
// ---------------------------------------------------------------------------

@description('Azure region for the storage account.')
param location string

@description('Globally-unique storage account name (3-24 lowercase alphanumerics).')
param storageAccountName string

@description('Blob container name for pgBackRest backups + WAL.')
param backupContainerName string = 'pgbackrest'

@description('OIDC issuer URL of the AKS cluster (from aks.bicep output).')
param oidcIssuerUrl string

@description('Federated subject for the pgBackRest pods, e.g. system:serviceaccount:<ns>:<sa>.')
param serviceAccountSubject string = 'system:serviceaccount:memex:pgbackrest-sa'

@description('Days to retain soft-deleted blobs / containers.')
param softDeleteRetentionDays int = 30

@description('Tags applied to every resource.')
param tags object = {}

resource storage 'Microsoft.Storage/storageAccounts@2024-01-01' = {
  name: storageAccountName
  location: location
  tags: tags
  kind: 'StorageV2'
  sku: {
    name: 'Standard_ZRS' // zone-redundant: backups survive a single-AZ loss
  }
  properties: {
    minimumTlsVersion: 'TLS1_2'
    allowBlobPublicAccess: false
    supportsHttpsTrafficOnly: true
    accessTier: 'Hot'
  }
}

resource blobService 'Microsoft.Storage/storageAccounts/blobServices@2024-01-01' = {
  parent: storage
  name: 'default'
  properties: {
    deleteRetentionPolicy: {
      enabled: true
      days: softDeleteRetentionDays
    }
    containerDeleteRetentionPolicy: {
      enabled: true
      days: softDeleteRetentionDays
    }
  }
}

resource backupContainer 'Microsoft.Storage/storageAccounts/blobServices/containers@2024-01-01' = {
  parent: blobService
  name: backupContainerName
  properties: {
    publicAccess: 'None'
  }
}

// --- Workload Identity: keyless access for the pgBackRest pods --------------
resource backupIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2024-11-30' = {
  name: '${storageAccountName}-pgbackrest-mi'
  location: location
  tags: tags
}

// "Storage Blob Data Contributor" so pgBackRest can read+write blobs.
var blobDataContributorRoleId = 'ba92f5b4-2d11-453d-a403-e96b0029c9fe'

resource backupBlobRoleAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(storage.id, backupIdentity.id, blobDataContributorRoleId)
  scope: storage
  properties: {
    principalId: backupIdentity.properties.principalId
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', blobDataContributorRoleId)
    principalType: 'ServicePrincipal'
  }
}

// Federate the managed identity with the pgBackRest Kubernetes service account.
resource federatedCredential 'Microsoft.ManagedIdentity/userAssignedIdentities/federatedIdentityCredentials@2024-11-30' = {
  parent: backupIdentity
  name: 'pgbackrest-federated'
  properties: {
    issuer: oidcIssuerUrl
    subject: serviceAccountSubject
    audiences: [
      'api://AzureADTokenExchange'
    ]
  }
}

output storageAccountName string = storage.name
output storageAccountId string = storage.id
output backupContainerName string = backupContainer.name
output blobEndpoint string = storage.properties.primaryEndpoints.blob
output backupIdentityClientId string = backupIdentity.properties.clientId
output backupIdentityId string = backupIdentity.id
