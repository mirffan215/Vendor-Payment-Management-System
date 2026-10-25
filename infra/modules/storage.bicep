param prefix string
param location string
param tags object
@description('Standard_GZRS or Standard_RAGZRS in production; Standard_LRS is enough for development')
param skuName string = 'Standard_GZRS'
param publicNetworkAccess string = 'Enabled'
param privateEndpointSubnetId string = ''
param blobZoneId string = ''
param apiPrincipalId string
param workerPrincipalId string
param migratorPrincipalId string
param monitoringWorkspaceId string
param lockResource bool = false
@description('Days that files deleted by mistake can be recovered')
param softDeleteDays int = 30

// Files (attachments, generated documents) and the data-protection key ring. Versioning and soft delete protect against mistakes and bad deployments.
resource sa 'Microsoft.Storage/storageAccounts@2023-05-01' = {
  name: take('${replace(prefix, '-', '')}st${uniqueString(resourceGroup().id)}', 24)
  location: location
  tags: tags
  sku: { name: skuName }
  kind: 'StorageV2'
  properties: {
    minimumTlsVersion: 'TLS1_2'
    allowBlobPublicAccess: false
    allowSharedKeyAccess: false
    supportsHttpsTrafficOnly: true
    defaultToOAuthAuthentication: true
    publicNetworkAccess: publicNetworkAccess
    networkAcls: { defaultAction: publicNetworkAccess == 'Enabled' ? 'Allow' : 'Deny', bypass: 'AzureServices' }
    encryption: { services: { blob: { enabled: true, keyType: 'Account' } }, keySource: 'Microsoft.Storage' }
  }
}

resource blobService 'Microsoft.Storage/storageAccounts/blobServices@2023-05-01' = {
  parent: sa
  name: 'default'
  properties: {
    isVersioningEnabled: true
    deleteRetentionPolicy: { enabled: true, days: softDeleteDays }
    containerDeleteRetentionPolicy: { enabled: true, days: softDeleteDays }
    restorePolicy: { enabled: false }
  }
}

resource files 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = { parent: blobService, name: 'vpms', properties: { publicAccess: 'None' } }
resource keyRing 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = { parent: blobService, name: 'dataprotection', properties: { publicAccess: 'None' } }

var blobContributor = 'ba92f5b4-2d11-453d-a403-e96b0029c9fe'

var writers = [
  { key: 'api', principalId: apiPrincipalId }
  { key: 'worker', principalId: workerPrincipalId }
  { key: 'migrator', principalId: migratorPrincipalId }
]

resource access 'Microsoft.Authorization/roleAssignments@2022-04-01' = [for w in writers: {
  name: guid(sa.id, w.principalId, blobContributor)
  scope: sa
  properties: { principalId: w.principalId, principalType: 'ServicePrincipal', roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', blobContributor) }
}]

resource diagnostics 'Microsoft.Insights/diagnosticSettings@2021-05-01-preview' = {
  scope: blobService
  name: 'to-logs'
  properties: {
    workspaceId: monitoringWorkspaceId
    logs: [{ category: 'StorageRead', enabled: true }, { category: 'StorageWrite', enabled: true }, { category: 'StorageDelete', enabled: true }]
    metrics: [{ category: 'Transaction', enabled: true }]
  }
}

module endpoint 'privateendpoint.bicep' = if (!empty(privateEndpointSubnetId)) {
  name: 'st-pe'
  params: { name: '${prefix}-blob-pe', location: location, tags: tags, subnetId: privateEndpointSubnetId, targetResourceId: sa.id, groupId: 'blob', dnsZoneId: blobZoneId }
}

resource noDelete 'Microsoft.Authorization/locks@2020-05-01' = if (lockResource) {
  scope: sa
  name: 'no-delete'
  properties: { level: 'CanNotDelete', notes: 'Customer documents and the data-protection key ring.' }
}

output name string = sa.name
output blobUrl string = sa.properties.primaryEndpoints.blob
output id string = sa.id
