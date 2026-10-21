param prefix string
param location string

resource logs 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: '${prefix}-logs'
  location: location
  properties: { sku: { name: 'PerGB2018' }, retentionInDays: 90 }
}

resource appi 'Microsoft.Insights/components@2020-02-02' = {
  name: '${prefix}-appi'
  location: location
  kind: 'web'
  properties: { Application_Type: 'web', WorkspaceResourceId: logs.id }
}

output workspaceId string = logs.id
