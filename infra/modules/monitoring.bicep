param prefix string
param location string
param tags object
@description('Days logs are kept. Security-relevant history (sign-in refusals, audit chain alerts) must outlive an investigation.')
param retentionInDays int = 365

resource logs 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: '${prefix}-logs'
  location: location
  tags: tags
  properties: {
    sku: { name: 'PerGB2018' }
    retentionInDays: retentionInDays
    publicNetworkAccessForIngestion: 'Enabled'
    publicNetworkAccessForQuery: 'Enabled'
  }
}

resource appi 'Microsoft.Insights/components@2020-02-02' = {
  name: '${prefix}-appi'
  location: location
  kind: 'web'
  tags: tags
  properties: { Application_Type: 'web', WorkspaceResourceId: logs.id, DisableLocalAuth: false, RetentionInDays: 90 }
}

output workspaceId string = logs.id
output workspaceName string = logs.name
output appInsightsId string = appi.id
output appInsightsConnectionString string = appi.properties.ConnectionString
