// Container registry. Premium for private endpoint, geo-replication and content trust; images are pulled by managed identity only.
param prefix string
param location string
param tags object
param sku string = 'Premium'
param publicNetworkAccess string = 'Enabled'
param privateEndpointSubnetId string = ''
param registryZoneId string = ''
param pullPrincipalIds array
param monitoringWorkspaceId string

resource acr 'Microsoft.ContainerRegistry/registries@2023-11-01-preview' = {
  name: take('${replace(prefix, '-', '')}acr${uniqueString(resourceGroup().id)}', 50)
  location: location
  tags: tags
  sku: { name: sku }
  properties: {
    adminUserEnabled: false
    anonymousPullEnabled: false
    publicNetworkAccess: publicNetworkAccess
    networkRuleBypassOptions: 'AzureServices'
    policies: {
      retentionPolicy: { status: 'enabled', days: 30 }
      softDeletePolicy: { status: 'enabled', retentionDays: 30 }
    }
  }
}

// AcrPull
var acrPull = '7f951dda-4ed3-4680-a7ca-43fe172d538d'
resource pull 'Microsoft.Authorization/roleAssignments@2022-04-01' = [for p in pullPrincipalIds: {
  scope: acr
  name: guid(acr.id, p, acrPull)
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', acrPull)
    principalId: p
    principalType: 'ServicePrincipal'
  }
}]

resource diagnostics 'Microsoft.Insights/diagnosticSettings@2021-05-01-preview' = {
  scope: acr
  name: 'to-logs'
  properties: {
    workspaceId: monitoringWorkspaceId
    logs: [
      { category: 'ContainerRegistryRepositoryEvents', enabled: true }
      { category: 'ContainerRegistryLoginEvents', enabled: true }
    ]
  }
}

module endpoint 'privateendpoint.bicep' = if (!empty(privateEndpointSubnetId)) {
  name: 'acr-pe'
  params: {
    name: '${prefix}-acr-pe'
    location: location
    tags: tags
    subnetId: privateEndpointSubnetId
    targetResourceId: acr.id
    groupId: 'registry'
    dnsZoneId: registryZoneId
  }
}

output name string = acr.name
output loginServer string = acr.properties.loginServer
output id string = acr.id
