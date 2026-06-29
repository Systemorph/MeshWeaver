// ---------------------------------------------------------------------------
// files.bicep — A dedicated Azure Files storage account holding the named SMB
//               shares ("drives") that the portal + observability stack mount.
//
// WHY A SEPARATE ACCOUNT (vs. dynamic azurefile provisioning)?
// ------------------------------------------------------------
// The default path uses the `azurefile-memex` StorageClass to DYNAMICALLY create
// a share per PVC (simplest — see manifests/storageclass-azurefile.yaml). This
// module is the OPTIONAL static-binding path: operators who want pre-created,
// named, individually-sized/quota'd shares (and a single account to back up,
// firewall, or lifecycle-manage) provision this account, then bind STATIC PVs to
// the named shares via the file.csi.azure.com driver (see README "Static PV
// binding"). Dynamic stays the default; this is here for operators who prefer
// named drives.
//
// Shares (one "drive" per concern, matching the /data + /mnt/* mounts):
//   data        -> /data            framework caches (DataProtection keys, caches)
//   content     -> /mnt/content     content collection (Storage__BasePath)
//   attachments -> /mnt/attachments attachments collection
//   users       -> /mnt/users       co-hosted CLI configs
//   otel-logs   -> /mnt/otel-logs   OpenTelemetry Collector file-exporter archive
//
// StorageV2 + Standard_ZRS (zone-redundant) + largeFileSharesState=Enabled so
// individual shares can exceed 5 TiB (up to 100 TiB). SMB multichannel left at
// default. This is a content/log account — NOT the pgBackRest BLOB account in
// storage.bicep; keeping them separate isolates blast radius and access policy.
// ---------------------------------------------------------------------------

@description('Azure region for the Files storage account.')
param location string

@description('Globally-unique storage account name (3-24 lowercase alphanumerics).')
param storageAccountName string

@description('Quota (GiB) for the content-collection share.')
param contentShareQuotaGib int = 128

@description('Quota (GiB) for the attachments share.')
param attachmentsShareQuotaGib int = 64

@description('Quota (GiB) for the framework-cache /data share.')
param dataShareQuotaGib int = 16

@description('Quota (GiB) for the co-hosted CLI-config /mnt/users share.')
param usersShareQuotaGib int = 32

@description('Quota (GiB) for the OpenTelemetry Collector log-archive share.')
param otelLogsShareQuotaGib int = 64

@description('Tags applied to every resource.')
param tags object = {}

resource files 'Microsoft.Storage/storageAccounts@2024-01-01' = {
  name: storageAccountName
  location: location
  tags: tags
  kind: 'StorageV2'
  sku: {
    // Zone-redundant: shares survive a single-AZ loss, matching the 3-zone
    // node spread. Switch to Standard_LRS to cut cost where ZRS isn't needed,
    // or Premium_ZRS (FileStorage kind) for IOPS-heavy content.
    name: 'Standard_ZRS'
  }
  properties: {
    // Allow >5 TiB shares (up to 100 TiB) so content can grow without re-homing.
    largeFileSharesState: 'Enabled'
    minimumTlsVersion: 'TLS1_2'
    allowBlobPublicAccess: false
    supportsHttpsTrafficOnly: true
    accessTier: 'Hot'
  }
}

resource fileService 'Microsoft.Storage/storageAccounts/fileServices@2024-01-01' = {
  parent: files
  name: 'default'
  properties: {
    // 14-day share soft-delete: an accidental `kubectl delete pvc` (with a Delete
    // reclaimPolicy) or share drop is recoverable.
    shareDeleteRetentionPolicy: {
      enabled: true
      days: 14
    }
  }
}

resource contentShare 'Microsoft.Storage/storageAccounts/fileServices/shares@2024-01-01' = {
  parent: fileService
  name: 'content'
  properties: {
    shareQuota: contentShareQuotaGib
    enabledProtocols: 'SMB'
  }
}

resource attachmentsShare 'Microsoft.Storage/storageAccounts/fileServices/shares@2024-01-01' = {
  parent: fileService
  name: 'attachments'
  properties: {
    shareQuota: attachmentsShareQuotaGib
    enabledProtocols: 'SMB'
  }
}

resource dataShare 'Microsoft.Storage/storageAccounts/fileServices/shares@2024-01-01' = {
  parent: fileService
  name: 'data'
  properties: {
    shareQuota: dataShareQuotaGib
    enabledProtocols: 'SMB'
  }
}

resource usersShare 'Microsoft.Storage/storageAccounts/fileServices/shares@2024-01-01' = {
  parent: fileService
  name: 'users'
  properties: {
    shareQuota: usersShareQuotaGib
    enabledProtocols: 'SMB'
  }
}

resource otelLogsShare 'Microsoft.Storage/storageAccounts/fileServices/shares@2024-01-01' = {
  parent: fileService
  name: 'otel-logs'
  properties: {
    shareQuota: otelLogsShareQuotaGib
    enabledProtocols: 'SMB'
  }
}

output storageAccountName string = files.name
output storageAccountId string = files.id
output fileEndpoint string = files.properties.primaryEndpoints.file
output contentShareName string = contentShare.name
output attachmentsShareName string = attachmentsShare.name
output dataShareName string = dataShare.name
output usersShareName string = usersShare.name
output otelLogsShareName string = otelLogsShare.name
