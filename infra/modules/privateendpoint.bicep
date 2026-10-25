param name string
param location string
param tags object
param subnetId string
param targetResourceId string
@description('The sub-resource: vault, blob, sqlServer, registry')
param groupId string
param dnsZoneId string

resource pe 'Microsoft.Network/privateEndpoints@2024-01-01' = {
  name: name
  location: location
  tags: tags
  properties: {
    subnet: { id: subnetId }
    privateLinkServiceConnections: [{ name: name, properties: { privateLinkServiceId: targetResourceId, groupIds: [groupId] } }]
  }
}

resource zoneGroup 'Microsoft.Network/privateEndpoints/privateDnsZoneGroups@2024-01-01' = {
  parent: pe
  name: 'default'
  properties: { privateDnsZoneConfigs: [{ name: 'config', properties: { privateDnsZoneId: dnsZoneId } }] }
}
