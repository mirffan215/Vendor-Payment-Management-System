param prefix string
param location string
param tags object
param skuName string = 'Standard_GZRS'
param publicNetworkAccess string = 'Enabled'
param privateEndpointSubnetId string = ''
param blobZoneId string = ''
param apiPrincipalId string
param workerPrincipalId string
param migratorPrincipalId string
param monitoringWorkspaceId string
param lockResource bool = false
@description('How long an anchor cannot be changed or deleted by anyone, including administrators. Should be at least the audit retention.')
param immutabilityDays int = 3650

// The audit anchors live in their own storage account: separate access rules, no versioning (it does not combine with container-level immutability),
// and an immutability policy so even a subscription owner cannot rewrite history. The policy is created unlocked; locking it is a deliberate, irreversible
// step an administrator performs once the retention period has been agreed (docs/operations/deployment.md).
resource sa 'Microsoft.Storage/storageAccounts@2023-05-01' = {
  name: take('${replace(prefix, '-', '')}an${uniqueString(resourceGroup().id)}', 24)
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
  }
}

resource blobService 'Microsoft.Storage/storageAccounts/blobServices@2023-05-01' = {
  parent: sa
  name: 'default'
  properties: { deleteRetentionPolicy: { enabled: true, days: 30 }, containerDeleteRetentionPolicy: { enabled: true, days: 30 } }
}

resource anchors 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = {
  parent: blobService
  name: 'audit-anchors'
  properties: { publicAccess: 'None' }
}

resource immutability 'Microsoft.Storage/storageAccounts/blobServices/containers/immutabilityPolicies@2023-05-01' = {
  parent: anchors
  name: 'default'
  properties: { immutabilityPeriodSinceCreationInDays: immutabilityDays, allowProtectedAppendWrites: false }
}

var blobContributor = 'ba92f5b4-2d11-453d-a403-e96b0029c9fe'
var blobReader = '2a2b9908-6ea1-4ae2-8e65-a410df84e7d1'

// The API and worker publish anchors (write-once); the migrator only reads them when it verifies a restore.
resource writeAccess 'Microsoft.Authorization/roleAssignments@2022-04-01' = [for p in [apiPrincipalId, workerPrincipalId]: {
  name: guid(sa.id, p, blobContributor)
  scope: sa
  properties: { principalId: p, principalType: 'ServicePrincipal', roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', blobContributor) }
}]

resource readAccess 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(sa.id, migratorPrincipalId, blobReader)
  scope: sa
  properties: { principalId: migratorPrincipalId, principalType: 'ServicePrincipal', roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', blobReader) }
}

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
  name: 'anch-pe'
  params: { name: '${prefix}-anchors-pe', location: location, tags: tags, subnetId: privateEndpointSubnetId, targetResourceId: sa.id, groupId: 'blob', dnsZoneId: blobZoneId }
}

resource noDelete 'Microsoft.Authorization/locks@2020-05-01' = if (lockResource) {
  scope: sa
  name: 'no-delete'
  properties: { level: 'CanNotDelete', notes: 'Audit anchors (immutable). See docs/operations/deployment.md.' }
}

output blobUrl string = sa.properties.primaryEndpoints.blob
output name string = sa.name
