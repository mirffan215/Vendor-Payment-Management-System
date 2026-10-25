param prefix string
param location string
param tags object
@description('Entra object id that can manage secrets (the deployment identity)')
param adminObjectId string
param apiPrincipalId string
param workerPrincipalId string
param migratorPrincipalId string
@description('Disabled when private endpoints are used')
param publicNetworkAccess string = 'Enabled'
param privateEndpointSubnetId string = ''
param vaultZoneId string = ''
param monitoringWorkspaceId string
param lockResource bool = false

resource kv 'Microsoft.KeyVault/vaults@2023-07-01' = {
  name: take('${prefix}-kv-${uniqueString(resourceGroup().id)}', 24)
  location: location
  tags: tags
  properties: {
    tenantId: subscription().tenantId
    sku: { family: 'A', name: 'standard' }
    enableRbacAuthorization: true
    enableSoftDelete: true
    softDeleteRetentionInDays: 90
    enablePurgeProtection: true
    publicNetworkAccess: publicNetworkAccess
    networkAcls: { defaultAction: publicNetworkAccess == 'Enabled' ? 'Allow' : 'Deny', bypass: 'AzureServices' }
  }
}

// The key that wraps every tenant's data keys (ADR-0004). It never leaves the vault: the application asks the vault to wrap and unwrap.
resource kek 'Microsoft.KeyVault/vaults/keys@2023-07-01' = {
  parent: kv
  name: 'vpms-kek'
  properties: {
    kty: 'RSA'
    keySize: 3072
    keyOps: ['wrapKey', 'unwrapKey']
    attributes: { enabled: true }
    rotationPolicy: {
      lifetimeActions: [{ trigger: { timeAfterCreate: 'P1Y' }, action: { type: 'rotate' } }, { trigger: { timeBeforeExpiry: 'P30D' }, action: { type: 'notify' } }]
      attributes: { expiryTime: 'P2Y' }
    }
  }
}

// The key that protects the ASP.NET data-protection key ring at rest (sessions, anti-forgery, sign-in challenges).
resource dataProtectionKey 'Microsoft.KeyVault/vaults/keys@2023-07-01' = {
  parent: kv
  name: 'vpms-dataprotection'
  properties: {
    kty: 'RSA'
    keySize: 3072
    keyOps: ['wrapKey', 'unwrapKey']
    attributes: { enabled: true }
  }
}

var secretsOfficer = 'b86a8fe4-44ce-4948-aee5-eccb2c155cd7'
var secretsUser = '4633458b-17de-408a-b874-0e1b2c5e1c2e'
var cryptoUser = '12338af0-0e69-4776-bea7-57ae8d297424'

resource admin 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(kv.id, adminObjectId, secretsOfficer)
  scope: kv
  properties: { principalId: adminObjectId, roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', secretsOfficer) }
}

var readers = [
  { key: 'api', principalId: apiPrincipalId }
  { key: 'worker', principalId: workerPrincipalId }
  { key: 'migrator', principalId: migratorPrincipalId }
]

resource secretReaders 'Microsoft.Authorization/roleAssignments@2022-04-01' = [for r in readers: {
  name: guid(kv.id, r.principalId, secretsUser)
  scope: kv
  properties: { principalId: r.principalId, principalType: 'ServicePrincipal', roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', secretsUser) }
}]

// Wrapping keys is for the API and the worker (the migrator's restore check unwraps one key to prove the vault is reachable).
resource cryptoUsers 'Microsoft.Authorization/roleAssignments@2022-04-01' = [for r in readers: {
  name: guid(kv.id, r.principalId, cryptoUser)
  scope: kv
  properties: { principalId: r.principalId, principalType: 'ServicePrincipal', roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', cryptoUser) }
}]

resource diagnostics 'Microsoft.Insights/diagnosticSettings@2021-05-01-preview' = {
  scope: kv
  name: 'to-logs'
  properties: { workspaceId: monitoringWorkspaceId, logs: [{ categoryGroup: 'audit', enabled: true }], metrics: [{ category: 'AllMetrics', enabled: true }] }
}

module endpoint 'privateendpoint.bicep' = if (!empty(privateEndpointSubnetId)) {
  name: 'kv-pe'
  params: { name: '${prefix}-kv-pe', location: location, tags: tags, subnetId: privateEndpointSubnetId, targetResourceId: kv.id, groupId: 'vault', dnsZoneId: vaultZoneId }
}

resource noDelete 'Microsoft.Authorization/locks@2020-05-01' = if (lockResource) {
  scope: kv
  name: 'no-delete'
  properties: { level: 'CanNotDelete', notes: 'Holds the key-encryption key; destroying it destroys every tenant data set.' }
}

output name string = kv.name
output uri string = kv.properties.vaultUri
output kekKeyId string = kek.properties.keyUriWithVersion
output dataProtectionKeyId string = dataProtectionKey.properties.keyUri
