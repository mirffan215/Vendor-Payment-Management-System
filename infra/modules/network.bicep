param prefix string
param location string
param tags object
param addressPrefix string = '10.40.0.0/16'

resource vnet 'Microsoft.Network/virtualNetworks@2024-01-01' = {
  name: '${prefix}-vnet'
  location: location
  tags: tags
  properties: {
    addressSpace: { addressPrefixes: [addressPrefix] }
    subnets: [
      {
        // Container Apps environment (workload profiles need at least a /27; a /23 leaves room to grow).
        name: 'snet-apps'
        properties: {
          addressPrefix: cidrSubnet(addressPrefix, 23, 0)
          delegations: [{ name: 'apps', properties: { serviceName: 'Microsoft.App/environments' } }]
        }
      }
      {
        name: 'snet-private-endpoints'
        properties: { addressPrefix: cidrSubnet(addressPrefix, 24, 2), privateEndpointNetworkPolicies: 'Disabled' }
      }
    ]
  }
}

var zones = [
  'privatelink${environment().suffixes.sqlServerHostname}'
  'privatelink.blob.${environment().suffixes.storage}'
  'privatelink.vaultcore.azure.net'
  'privatelink.azurecr.io'
]

resource dns 'Microsoft.Network/privateDnsZones@2020-06-01' = [for zone in zones: {
  name: zone
  location: 'global'
  tags: tags
}]

resource links 'Microsoft.Network/privateDnsZones/virtualNetworkLinks@2020-06-01' = [for (zone, i) in zones: {
  parent: dns[i]
  name: '${prefix}-link'
  location: 'global'
  properties: { virtualNetwork: { id: vnet.id }, registrationEnabled: false }
}]

output appsSubnetPrefix string = cidrSubnet(addressPrefix, 23, 0)
output appsSubnetId string = vnet.properties.subnets[0].id
output privateEndpointSubnetId string = vnet.properties.subnets[1].id
output sqlZoneId string = dns[0].id
output blobZoneId string = dns[1].id
output vaultZoneId string = dns[2].id
output registryZoneId string = dns[3].id
