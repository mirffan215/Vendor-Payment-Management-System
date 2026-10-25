// Azure SQL: Entra-only authentication (no SQL logins, no passwords), TLS 1.2, TDE, auditing to Log Analytics,
// point-in-time restore for 35 days and long-term backups. The application connects as managed identities (see the migrator).
param prefix string
param location string
param tags object
@description('Entra group (or user) that administers the server. The deployment/migrator identity must be a member.')
param adminObjectId string
param adminName string
param tenantId string = tenant().tenantId
param skuName string = 'GP_Gen5'
param skuTier string = 'GeneralPurpose'
param capacity int = 2
param zoneRedundant bool = false
param maxSizeGb int = 64
param publicNetworkAccess string = 'Enabled'
param privateEndpointSubnetId string = ''
param sqlZoneId string = ''
param monitoringWorkspaceId string
param lockResource bool = false
@description('Backup storage redundancy. Geo keeps backups restorable in the paired region.')
@allowed(['Local', 'Zone', 'Geo', 'GeoZone'])
param backupRedundancy string = 'Geo'
param pitrDays int = 35

resource server 'Microsoft.Sql/servers@2023-08-01-preview' = {
  name: '${prefix}-sql-${uniqueString(resourceGroup().id)}'
  location: location
  tags: tags
  properties: {
    minimalTlsVersion: '1.2'
    publicNetworkAccess: publicNetworkAccess
    restrictOutboundNetworkAccess: 'Disabled'
    administrators: {
      administratorType: 'ActiveDirectory'
      principalType: 'Group'
      login: adminName
      sid: adminObjectId
      tenantId: tenantId
      azureADOnlyAuthentication: true
    }
  }
}

resource db 'Microsoft.Sql/servers/databases@2023-08-01-preview' = {
  parent: server
  name: 'Vpms'
  location: location
  tags: tags
  sku: { name: skuName, tier: skuTier, capacity: capacity }
  properties: {
    collation: 'SQL_Latin1_General_CP1_CI_AS'
    maxSizeBytes: maxSizeGb * 1073741824
    zoneRedundant: zoneRedundant
    requestedBackupStorageRedundancy: backupRedundancy
    readScale: 'Disabled'
  }
}

resource tde 'Microsoft.Sql/servers/databases/transparentDataEncryption@2023-08-01-preview' = {
  parent: db
  name: 'current'
  properties: { state: 'Enabled' }
}

resource pitr 'Microsoft.Sql/servers/databases/backupShortTermRetentionPolicies@2023-08-01-preview' = {
  parent: db
  name: 'default'
  properties: { retentionDays: pitrDays, diffBackupIntervalInHours: 12 }
}

resource ltr 'Microsoft.Sql/servers/databases/backupLongTermRetentionPolicies@2023-08-01-preview' = {
  parent: db
  name: 'default'
  properties: { weeklyRetention: 'P5W', monthlyRetention: 'P12M', yearlyRetention: 'P7Y', weekOfYear: 1 }
}

resource audit 'Microsoft.Sql/servers/auditingSettings@2023-08-01-preview' = {
  parent: server
  name: 'default'
  properties: {
    state: 'Enabled'
    isAzureMonitorTargetEnabled: true
    auditActionsAndGroups: [
      'SUCCESSFUL_DATABASE_AUTHENTICATION_GROUP'
      'FAILED_DATABASE_AUTHENTICATION_GROUP'
      'BATCH_COMPLETED_GROUP'
      'DATABASE_PERMISSION_CHANGE_GROUP'
      'DATABASE_ROLE_MEMBER_CHANGE_GROUP'
      'SCHEMA_OBJECT_PERMISSION_CHANGE_GROUP'
    ]
  }
}

// Audit events reach Log Analytics through the master database's diagnostic setting.
resource master 'Microsoft.Sql/servers/databases@2023-08-01-preview' existing = {
  parent: server
  name: 'master'
}

resource auditLogs 'Microsoft.Insights/diagnosticSettings@2021-05-01-preview' = {
  scope: master
  name: 'audit-to-logs'
  properties: {
    workspaceId: monitoringWorkspaceId
    logs: [
      { category: 'SQLSecurityAuditEvents', enabled: true }
    ]
  }
  dependsOn: [audit]
}

resource dbDiagnostics 'Microsoft.Insights/diagnosticSettings@2021-05-01-preview' = {
  scope: db
  name: 'to-logs'
  properties: {
    workspaceId: monitoringWorkspaceId
    logs: [
      { category: 'Errors', enabled: true }
      { category: 'Timeouts', enabled: true }
      { category: 'Blocks', enabled: true }
      { category: 'Deadlocks', enabled: true }
    ]
    metrics: [
      { category: 'Basic', enabled: true }
    ]
  }
}

resource vulnerability 'Microsoft.Sql/servers/sqlVulnerabilityAssessments@2023-08-01-preview' = {
  parent: server
  name: 'default'
  properties: { state: 'Enabled' }
}

module endpoint 'privateendpoint.bicep' = if (!empty(privateEndpointSubnetId)) {
  name: 'sql-pe'
  params: {
    name: '${prefix}-sql-pe'
    location: location
    tags: tags
    subnetId: privateEndpointSubnetId
    targetResourceId: server.id
    groupId: 'sqlServer'
    dnsZoneId: sqlZoneId
  }
}

resource noDelete 'Microsoft.Authorization/locks@2020-05-01' = if (lockResource) {
  scope: server
  name: 'no-delete'
  properties: { level: 'CanNotDelete', notes: 'The VPMS database.' }
}

output serverName string = server.name
output fqdn string = server.properties.fullyQualifiedDomainName
output databaseName string = db.name
