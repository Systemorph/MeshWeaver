// ---------------------------------------------------------------------------
// postgres.bicep — PRIVATE (VNet-injected) Azure Database for PostgreSQL
//                  Flexible Server with pgvector, matching the private-everything
//                  style of this sample.
//
// This is the MANAGED, PRIVATE database option: a Flexible Server injected into
// the delegated `postgres` subnet (network.bicep), reachable ONLY from inside the
// VNet (and over the P2S VPN) via the linked private DNS zone. No public endpoint.
// It replaces the in-cluster self-managed Postgres StatefulSet + pgBackRest when
// you want managed PITR (automatic backups + WAL, restore to any second in the
// retention window) and no in-cluster database moving parts.
//
// Toggle in main.bicep with `deployPostgresFlexible=true`; when on, also set
// `deployBackupStorage=false` and DON'T apply the postgres-pvc / pgbackrest
// manifests — point the portal's connection string at the output FQDN instead.
//
// pgvector: enabled via the `azure.extensions` server parameter (the portal's
// embeddings + HNSW vector search need it). The DB itself is created here.
// ---------------------------------------------------------------------------

@description('Azure region for the Flexible Server.')
param location string

@description('Flexible Server name (becomes <name>.postgres.database.azure.com).')
param serverName string

@description('Resource id of the DELEGATED subnet for VNet injection (network.outputs.postgresSubnetId).')
param delegatedSubnetId string

@description('Resource id of the private DNS zone *.private.postgres.database.azure.com (network.outputs.postgresPrivateDnsZoneId).')
param privateDnsZoneId string

@description('Administrator login name.')
param administratorLogin string = 'memexadmin'

@description('Administrator password. Pass at deploy time; never commit a real one.')
@secure()
param administratorPassword string

@description('Compute SKU (e.g. Standard_D2ds_v5 = 2 vCPU / 8 GiB; Standard_D4ds_v5 = 4 vCPU / 16 GiB).')
param skuName string = 'Standard_D2ds_v5'

@description('Compute tier.')
@allowed([
  'Burstable'
  'GeneralPurpose'
  'MemoryOptimized'
])
param skuTier string = 'GeneralPurpose'

@description('Storage size in GiB.')
param storageSizeGib int = 128

@description('PostgreSQL major version.')
param postgresVersion string = '16'

@description('Backup retention in days (7-35) for managed PITR.')
@minValue(7)
@maxValue(35)
param backupRetentionDays int = 14

@description('Enable zone-redundant HA (a standby in a second zone). Costs ~2x compute.')
param highAvailability bool = true

@description('Name of the application database to create.')
param databaseName string = 'memex'

@description('Tags applied to every resource.')
param tags object = {}

resource postgres 'Microsoft.DBforPostgreSQL/flexibleServers@2024-08-01' = {
  name: serverName
  location: location
  tags: tags
  sku: {
    name: skuName
    tier: skuTier
  }
  properties: {
    version: postgresVersion
    administratorLogin: administratorLogin
    administratorLoginPassword: administratorPassword
    storage: {
      storageSizeGB: storageSizeGib
      autoGrow: 'Enabled'
    }
    backup: {
      backupRetentionDays: backupRetentionDays
      geoRedundantBackup: 'Disabled'
    }
    highAvailability: {
      // Zone-redundant HA pairs a hot standby in another AZ — matches the
      // 3-zone node spread. Set false (Disabled) to halve compute cost.
      mode: highAvailability ? 'ZoneRedundant' : 'Disabled'
    }
    // PRIVATE access: inject into the delegated subnet + private DNS zone.
    // No publicNetworkAccess endpoint is created in this mode.
    network: {
      delegatedSubnetResourceId: delegatedSubnetId
      privateDnsZoneArmResourceId: privateDnsZoneId
    }
  }
}

// Enable pgvector (and uuid-ossp, commonly needed) via allowlist server param.
// azure.extensions must list every extension before CREATE EXTENSION works.
resource azureExtensions 'Microsoft.DBforPostgreSQL/flexibleServers/configurations@2024-08-01' = {
  parent: postgres
  name: 'azure.extensions'
  properties: {
    value: 'VECTOR,UUID-OSSP'
    source: 'user-override'
  }
}

resource memexDatabase 'Microsoft.DBforPostgreSQL/flexibleServers/databases@2024-08-01' = {
  parent: postgres
  name: databaseName
  properties: {
    charset: 'UTF8'
    collation: 'en_US.utf8'
  }
  dependsOn: [
    azureExtensions
  ]
}

output serverName string = postgres.name
output serverId string = postgres.id
// Private FQDN — resolves to the VNet NIC IP only inside the VNet / over the VPN.
output fullyQualifiedDomainName string = postgres.properties.fullyQualifiedDomainName
output databaseName string = memexDatabase.name
output administratorLogin string = administratorLogin
