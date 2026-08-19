// ---------------------------------------------------------------------------
// backups.bicep — the fleet's LOGICAL database backup store, plus the identity
//                 the hosting operator runs as.
//
// This is NOT storage.bicep. That one serves pgBackRest's physical WAL/PITR
// archive for the self-managed in-cluster Postgres. The fleet's instances run
// on the shared Azure PostgreSQL Flexible Server, where the thing you need
// before dropping a database is a LOGICAL dump (`pg_dump -Fc`) of that one
// database — which server-level PITR cannot give you after the database is
// gone. The two have different lifetimes, different blast radii and different
// readers, so they get different accounts.
//
// What this deploys:
//   * a ZRS storage account + a `db-backups` container, versioned and
//     soft-deletable, with a lifecycle rule that expires dumps on a schedule;
//   * a user-assigned managed identity (`hosting-operator`) federated to the
//     operator ServiceAccount, holding the roles a lifecycle run needs;
//   * the role assignments that let it write dumps and read them back.
//
// 🚨 The operator identity is the most powerful thing in this file. It can
//    create and delete databases on the shared server and write to the backup
//    account. It is federated to ONE ServiceAccount in ONE namespace — the ops
//    namespace of the CONTROL instance — and to nothing else. Do not federate
//    it to a tenant namespace, and do not reuse it for the portal: the portal's
//    identity is `portal-identity.bicep`, and keeping them apart is what stops
//    a prompt injection in an in-pod AI CLI from reaching the control plane.
// ---------------------------------------------------------------------------

@description('Azure region for the storage account.')
param location string

@description('Globally-unique storage account name for database dumps (3-24 lowercase alphanumerics).')
param backupAccountName string

@description('Blob container database dumps are written to.')
param dumpContainerName string = 'db-backups'

@description('Days a dump is retained before the lifecycle rule deletes it. 0 disables the rule.')
param retentionDays int = 90

@description('OIDC issuer URL of the AKS cluster (from aks.bicep output).')
param oidcIssuerUrl string

@description('Namespace the hosting operator Jobs run in.')
param operatorNamespace string = 'memex-ops'

@description('ServiceAccount the hosting operator Jobs run as.')
param operatorServiceAccount string = 'hosting-operator'

@description('Name of the user-assigned identity the operator authenticates as.')
param operatorIdentityName string = 'hosting-operator'

@description('Resource id of the PostgreSQL Flexible Server instances live on. Empty skips that grant.')
param postgresServerId string = ''

@description('Tags applied to every resource.')
param tags object = {}

resource backups 'Microsoft.Storage/storageAccounts@2024-01-01' = {
  name: backupAccountName
  location: location
  tags: tags
  kind: 'StorageV2'
  sku: {
    // Zone-redundant: the reason a dump exists is that something went wrong, and
    // "the backup was in the AZ that failed" is not a story anyone wants to tell.
    name: 'Standard_ZRS'
  }
  properties: {
    minimumTlsVersion: 'TLS1_2'
    allowBlobPublicAccess: false
    supportsHttpsTrafficOnly: true
    // No shared-key auth. Every writer and reader of this account authenticates
    // as a principal, so every access is attributable — and a leaked account key
    // cannot exist because there is nothing that uses one.
    allowSharedKeyAccess: false
    accessTier: 'Hot'
  }
}

resource blobService 'Microsoft.Storage/storageAccounts/blobServices@2024-01-01' = {
  parent: backups
  name: 'default'
  properties: {
    // Soft delete is the backstop for the failure this whole file guards against:
    // a teardown that deleted the dump it had just taken.
    deleteRetentionPolicy: {
      enabled: true
      days: 30
    }
    containerDeleteRetentionPolicy: {
      enabled: true
      days: 30
    }
    isVersioningEnabled: true
  }
}

resource dumpContainer 'Microsoft.Storage/storageAccounts/blobServices/containers@2024-01-01' = {
  parent: blobService
  name: dumpContainerName
  properties: {
    publicAccess: 'None'
  }
}

// Expire dumps on the recorded schedule. The Hosting/BackupStore node records the
// SAME number as `retentionDays`, so a restore can be told its archive is past
// retention BEFORE it tries to download it — keep the two in step.
resource lifecycle 'Microsoft.Storage/storageAccounts/managementPolicies@2024-01-01' = if (retentionDays > 0) {
  parent: backups
  name: 'default'
  properties: {
    policy: {
      rules: [
        {
          name: 'expire-dumps'
          enabled: true
          type: 'Lifecycle'
          definition: {
            filters: {
              blobTypes: [ 'blockBlob' ]
              prefixMatch: [ dumpContainerName ]
            }
            actions: {
              baseBlob: {
                delete: {
                  daysAfterModificationGreaterThan: retentionDays
                }
              }
            }
          }
        }
      ]
    }
  }
}

// --- The operator identity ---------------------------------------------------
resource operatorIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2024-11-30' = {
  name: operatorIdentityName
  location: location
  tags: tags
}

// Federated to exactly ONE ServiceAccount in ONE namespace. The subject must match
// `system:serviceaccount:<ns>:<sa>` exactly — a mismatch fails the token exchange
// at run time with an error that does not mention this file.
resource operatorFederation 'Microsoft.ManagedIdentity/userAssignedIdentities/federatedIdentityCredentials@2024-11-30' = {
  parent: operatorIdentity
  name: 'hosting-operator-federated'
  properties: {
    issuer: oidcIssuerUrl
    subject: 'system:serviceaccount:${operatorNamespace}:${operatorServiceAccount}'
    audiences: [ 'api://AzureADTokenExchange' ]
  }
}

var blobDataContributorRoleId = 'ba92f5b4-2d11-453d-a403-e96b0029c9fe'

resource operatorBlobRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(backups.id, operatorIdentity.id, blobDataContributorRoleId)
  scope: backups
  properties: {
    principalId: operatorIdentity.properties.principalId
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', blobDataContributorRoleId)
    principalType: 'ServicePrincipal'
  }
}

// Contributor on the PostgreSQL SERVER only — scoped to the one resource whose
// databases a lifecycle run creates and drops, never at resource-group scope.
var contributorRoleId = 'b24988ac-6180-42a0-ab88-20f7382dd24c'

resource existingPostgres 'Microsoft.DBforPostgreSQL/flexibleServers@2024-08-01' existing = if (!empty(postgresServerId)) {
  name: last(split(postgresServerId, '/'))
}

resource operatorPostgresRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (!empty(postgresServerId)) {
  name: guid(postgresServerId, operatorIdentity.id, contributorRoleId)
  scope: existingPostgres
  properties: {
    principalId: operatorIdentity.properties.principalId
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', contributorRoleId)
    principalType: 'ServicePrincipal'
  }
}

@description('Storage account holding database dumps — the BackupStore node\'s `account`.')
output backupAccountName string = backups.name

@description('Container dumps are written to — the BackupStore node\'s `container`.')
output dumpContainerName string = dumpContainer.name

@description('Blob endpoint of the backup account.')
output blobEndpoint string = backups.properties.primaryEndpoints.blob

@description('Client id of the operator identity — the BackupStore node\'s `credentialRef`, and AZURE_CLIENT_ID on the operator Jobs.')
output operatorIdentityClientId string = operatorIdentity.properties.clientId

@description('Resource id of the operator identity, for further role assignments (DNS zone, Key Vault).')
output operatorIdentityId string = operatorIdentity.id

@description('Principal id of the operator identity, for role assignments made outside this module.')
output operatorIdentityPrincipalId string = operatorIdentity.properties.principalId
