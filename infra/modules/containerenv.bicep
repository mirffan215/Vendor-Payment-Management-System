// The Container Apps environment: in the VNet, logs to Log Analytics. Separate from the apps so the edge (Front Door) can be given the default domain.
param prefix string
param location string
param tags object
param subnetId string
param monitoringWorkspaceName string
param internalOnly bool = false
param zoneRedundant bool = false

resource logs 'Microsoft.OperationalInsights/workspaces@2023-09-01' existing = {
  name: monitoringWorkspaceName
}

resource env 'Microsoft.App/managedEnvironments@2024-03-01' = {
  name: '${prefix}-env'
  location: location
  tags: tags
  properties: {
    zoneRedundant: zoneRedundant
    vnetConfiguration: { infrastructureSubnetId: subnetId, internal: internalOnly }
    appLogsConfiguration: {
      destination: 'log-analytics'
      logAnalyticsConfiguration: { customerId: logs.properties.customerId, sharedKey: logs.listKeys().primarySharedKey }
    }
    workloadProfiles: [{ name: 'Consumption', workloadProfileType: 'Consumption' }]
  }
}


output name string = env.name
output id string = env.id
output defaultDomain string = env.properties.defaultDomain
