// ---------------------------------------------------------------------------
// network.bicep — VNet + subnets + private DNS zone for the private AKS cluster.
//
// Subnet layout:
//   - aks-nodes      : the AKS node pool(s) live here (kubenet/Azure CNI overlay)
//   - GatewaySubnet  : RESERVED NAME (required by Azure) for the VPN Gateway
//   - bastion-subnet : optional AzureBastionSubnet for a jumpbox (alternative
//                      to the P2S VPN — see README; left empty by default)
//
// The private DNS zone privatelink.<region>.azmk8s.io is what makes a private
// AKS reachable: AKS publishes the API server's private IP into this zone, and
// linking the zone to the VNet means anything *inside* the VNet (including a
// P2S VPN client, which is logically attached to the VNet) resolves the API
// server FQDN to that private IP. Without this link, `kubectl` cannot find the
// control plane at all.
// ---------------------------------------------------------------------------

@description('Azure region for the network resources.')
param location string

@description('Resource name prefix (e.g. memex-aks).')
param namePrefix string

@description('Address space for the whole VNet.')
param vnetAddressSpace string = '10.42.0.0/16'

@description('Subnet CIDR for the AKS node pool.')
param aksSubnetPrefix string = '10.42.0.0/20'

@description('Subnet CIDR for the VPN GatewaySubnet (must be named GatewaySubnet).')
param gatewaySubnetPrefix string = '10.42.16.0/24'

@description('Subnet CIDR for an optional AzureBastionSubnet (jumpbox alternative).')
param bastionSubnetPrefix string = '10.42.17.0/26'

@description('Subnet CIDR for the delegated Azure Database for PostgreSQL Flexible Server subnet (private/VNet-injected PG).')
param postgresSubnetPrefix string = '10.42.18.0/24'

@description('Tags applied to every resource.')
param tags object = {}

// The private-link DNS zone name is region-specific. AKS expects exactly this
// shape; using anything else means the cluster cannot register its private IP.
var privateDnsZoneName = 'privatelink.${location}.azmk8s.io'

resource vnet 'Microsoft.Network/virtualNetworks@2024-05-01' = {
  name: '${namePrefix}-vnet'
  location: location
  tags: tags
  properties: {
    addressSpace: {
      addressPrefixes: [vnetAddressSpace]
    }
    subnets: [
      {
        name: 'aks-nodes'
        properties: {
          addressPrefix: aksSubnetPrefix
        }
      }
      {
        // RESERVED name — Azure VPN/ExpressRoute gateways MUST live in a subnet
        // literally called "GatewaySubnet". Do not rename.
        name: 'GatewaySubnet'
        properties: {
          addressPrefix: gatewaySubnetPrefix
        }
      }
      {
        // RESERVED name — Azure Bastion requires "AzureBastionSubnet".
        // Provisioned but unused unless you deploy Bastion (see README).
        name: 'AzureBastionSubnet'
        properties: {
          addressPrefix: bastionSubnetPrefix
        }
      }
      {
        // Delegated subnet for a PRIVATE (VNet-injected) PostgreSQL Flexible
        // Server. The delegation is mandatory: Flexible Server injects its NIC
        // here and the subnet can host nothing else. Used only when
        // deployPostgresFlexible=true (see postgres.bicep); harmless otherwise.
        name: 'postgres'
        properties: {
          addressPrefix: postgresSubnetPrefix
          delegations: [
            {
              name: 'fs-delegation'
              properties: {
                serviceName: 'Microsoft.DBforPostgreSQL/flexibleServers'
              }
            }
          ]
        }
      }
    ]
  }
}

// Private DNS zone for the VNet-injected Flexible Server. Flexible Server's
// private-access mode REQUIRES a zone named exactly *.private.postgres.database.azure.com
// linked to the VNet; the server's FQDN resolves to its private NIC IP only
// inside the VNet (and over the P2S VPN). Created here so the postgres module
// can attach to it; left unlinked-to-nothing-else if PG-flexible isn't deployed.
resource postgresPrivateDnsZone 'Microsoft.Network/privateDnsZones@2020-06-01' = {
  name: '${namePrefix}.private.postgres.database.azure.com'
  location: 'global'
  tags: tags
}

resource postgresPrivateDnsZoneVnetLink 'Microsoft.Network/privateDnsZones/virtualNetworkLinks@2020-06-01' = {
  parent: postgresPrivateDnsZone
  name: '${namePrefix}-pg-dnslink'
  location: 'global'
  tags: tags
  properties: {
    registrationEnabled: false
    virtualNetwork: {
      id: vnet.id
    }
  }
}

// Region-specific private DNS zone for the AKS private API server.
resource privateDnsZone 'Microsoft.Network/privateDnsZones@2020-06-01' = {
  name: privateDnsZoneName
  location: 'global'
  tags: tags
}

// Link the zone to the VNet so in-VNet clients (incl. P2S VPN) resolve the
// API server's private IP. registrationEnabled stays false — AKS writes the
// A record itself via its control-plane managed identity.
resource privateDnsZoneVnetLink 'Microsoft.Network/privateDnsZones/virtualNetworkLinks@2020-06-01' = {
  parent: privateDnsZone
  name: '${namePrefix}-dnslink'
  location: 'global'
  tags: tags
  properties: {
    registrationEnabled: false
    virtualNetwork: {
      id: vnet.id
    }
  }
}

output vnetId string = vnet.id
output vnetName string = vnet.name
output aksSubnetId string = '${vnet.id}/subnets/aks-nodes'
output gatewaySubnetId string = '${vnet.id}/subnets/GatewaySubnet'
output bastionSubnetId string = '${vnet.id}/subnets/AzureBastionSubnet'
output postgresSubnetId string = '${vnet.id}/subnets/postgres'
output privateDnsZoneId string = privateDnsZone.id
output privateDnsZoneName string = privateDnsZone.name
output postgresPrivateDnsZoneId string = postgresPrivateDnsZone.id
output postgresPrivateDnsZoneName string = postgresPrivateDnsZone.name
