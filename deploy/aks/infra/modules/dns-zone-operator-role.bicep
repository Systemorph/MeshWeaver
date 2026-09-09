// DNS Zone Contributor for the hosting operator identity on ONE public DNS zone — the grant
// `hosting-dns upsert` needs to create <instance>.<zone> A records on a Provision (and delete them
// on a Teardown). Its own module because the zone lives in a different resource group from the
// operator identity (backups.bicep), and a role assignment deploys at its target's scope.
//
// Deployed from backups.bicep when dnsZoneId is set, or on its own:
//   az deployment group create -g dns -f dns-zone-operator-role.bicep \
//     -p zoneName=meshweaver.cloud operatorPrincipalId=<operatorIdentityPrincipalId>
targetScope = 'resourceGroup'

@description('Name of the DNS zone in this resource group (e.g. meshweaver.cloud).')
param zoneName string

@description('Principal id of the hosting operator identity (backups.bicep output operatorIdentityPrincipalId).')
param operatorPrincipalId string

var dnsZoneContributorRoleId = 'befefa01-2a29-4197-83a8-272ff33ce314'

resource zone 'Microsoft.Network/dnsZones@2023-07-01-preview' existing = {
  name: zoneName
}

resource operatorDnsZoneRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(zone.id, operatorPrincipalId, dnsZoneContributorRoleId)
  scope: zone
  properties: {
    principalId: operatorPrincipalId
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', dnsZoneContributorRoleId)
    principalType: 'ServicePrincipal'
  }
}
