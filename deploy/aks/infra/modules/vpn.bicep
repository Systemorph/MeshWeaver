// ---------------------------------------------------------------------------
// vpn.bicep — Point-to-Site (P2S) VPN Gateway for reaching the PRIVATE AKS
//             API server with kubectl.
//
// Why this exists:
//   The cluster's API server has a private IP only (enablePrivateCluster=true).
//   An operator's laptop cannot reach it over the internet. A P2S VPN attaches
//   the laptop logically into the cluster VNet; combined with the private DNS
//   zone link (network.bicep), the operator then resolves the API server FQDN
//   to its private IP and `kubectl` works.
//
// Operator flow (full steps in README):
//   1. az deployment ... (this infra)
//   2. Generate root+client certs, upload root public cert (vpnClientRootCert)
//   3. Download the VPN client from the portal / `az network vnet-gateway vpn-client generate`
//   4. Connect the P2S VPN
//   5. az aks get-credentials --resource-group <rg> --name <cluster>
//   6. kubectl get nodes   # resolves the private API server over the tunnel
//
// Cert-based auth is used here (simplest, no Entra ID dependency). For prod you
// may prefer Azure AD (Entra) auth on the P2S — noted in README.
// ---------------------------------------------------------------------------

@description('Azure region for the gateway.')
param location string

@description('Resource name prefix (e.g. memex-aks).')
param namePrefix string

@description('Resource id of the GatewaySubnet (must be a subnet named GatewaySubnet).')
param gatewaySubnetId string

@description('Address pool handed out to P2S VPN clients. Must NOT overlap the VNet.')
param vpnClientAddressPool string = '172.16.201.0/24'

@description('Base64 public cert data of the P2S root certificate (no PEM headers, single line). Leave empty to deploy the gateway and add the cert later.')
param vpnClientRootCertData string = ''

@description('Friendly name for the uploaded root certificate.')
param vpnClientRootCertName string = 'P2SRootCert'

@description('VPN Gateway SKU. VpnGw1AZ is the cheapest that supports P2S + OpenVPN. Azure retired the non-AZ VpnGw1-5 SKUs (error NonAzSkusNotAllowedForVPNGateway) — only the zone-redundant *AZ SKUs can be created now.')
@allowed([
  'VpnGw1AZ'
  'VpnGw2AZ'
  'VpnGw3AZ'
])
param gatewaySku string = 'VpnGw1AZ'

@description('Tags applied to every resource.')
param tags object = {}

// Public IP for the gateway's tunnel endpoint (the data path stays private; the
// IKE/OpenVPN control endpoint needs a public IP — this is normal for P2S).
resource vpnPublicIp 'Microsoft.Network/publicIPAddresses@2024-05-01' = {
  name: '${namePrefix}-vpngw-pip'
  location: location
  tags: tags
  sku: {
    name: 'Standard'
  }
  zones: [
    '1'
    '2'
    '3'
  ]
  properties: {
    publicIPAllocationMethod: 'Static'
  }
}

resource vpnGateway 'Microsoft.Network/virtualNetworkGateways@2024-05-01' = {
  name: '${namePrefix}-vpngw'
  location: location
  tags: tags
  properties: {
    gatewayType: 'Vpn'
    vpnType: 'RouteBased'
    enableBgp: false
    activeActive: false
    sku: {
      name: gatewaySku
      tier: gatewaySku
    }
    ipConfigurations: [
      {
        name: 'vnetGatewayConfig'
        properties: {
          privateIPAllocationMethod: 'Dynamic'
          subnet: {
            id: gatewaySubnetId
          }
          publicIPAddress: {
            id: vpnPublicIp.id
          }
        }
      }
    ]
    // Point-to-Site configuration.
    vpnClientConfiguration: {
      vpnClientAddressPool: {
        addressPrefixes: [vpnClientAddressPool]
      }
      // OpenVPN supports the widest range of clients (incl. azure-vpn / OpenVPN
      // on Linux/Mac/Windows). IkeV2 added for native Windows clients.
      vpnClientProtocols: [
        'OpenVPN'
        'IkeV2'
      ]
      // Cert-based auth: upload the root public cert. If empty we skip it so the
      // gateway still deploys; add the cert afterwards with
      // `az network vnet-gateway root-cert create`.
      vpnClientRootCertificates: empty(vpnClientRootCertData) ? [] : [
        {
          name: vpnClientRootCertName
          properties: {
            publicCertData: vpnClientRootCertData
          }
        }
      ]
    }
  }
}

output vpnGatewayId string = vpnGateway.id
output vpnGatewayName string = vpnGateway.name
output vpnPublicIp string = vpnPublicIp.properties.ipAddress
