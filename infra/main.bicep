// VPMS platform for one environment (ADR-0025). Deploy in two steps: platform first (deployApps = false), then, once images are in the registry,
// the apps (deployApps = true; .github/workflows/deploy.yml does both). Nothing here ever holds a password: the apps and the migrator are managed
// identities, secrets live in Key Vault, SQL is Entra-only.
targetScope = 'resourceGroup'

@description('dev, test or prod.')
@allowed(['dev', 'test', 'prod'])
param environmentName string
param location string = resourceGroup().location
param workload string = 'vpms'

@description('Entra object id of the identity that deploys (gets Key Vault administration while keys are created).')
param deployerObjectId string
@description('Entra group that administers the SQL server. The deployer and the migrator identity must be members.')
param sqlAdminObjectId string
param sqlAdminName string

@description('Close the data services to the internet and reach them through private endpoints. Required for prod.')
param privateNetworking bool = false
@description('Put Azure Front Door + WAF in front of the web app.')
param enableFrontDoor bool = false
param zoneRedundant bool = false
param sqlSkuName string = 'GP_Gen5'
param sqlSkuTier string = 'GeneralPurpose'
param sqlCapacity int = 2
param sqlMaxSizeGb int = 64
param lockResources bool = false

@description('Public host name users type. Empty = the web app\'s own address (dev and test).')
param publicHost string = ''
@description('Front Door profile id for the web app to insist on (output frontDoorId of the first deployment).')
param frontDoorId string = ''

param deployApps bool = false
param imageTag string = 'none'
param revisionSuffix string = 'initial'
param apiTraffic array = [{ latestRevision: true, weight: 100 }]
param webTraffic array = [{ latestRevision: true, weight: 100 }]
param apiMinReplicas int = 1
param webMinReplicas int = 1

param alertEmails array = []
param alertWebhooks array = []

var prefix = '${workload}-${environmentName}'
var tags = {
  application: 'vpms'
  environment: environmentName
  'data-classification': 'confidential-financial'
  managedBy: 'bicep'
}
var closed = privateNetworking ? 'Disabled' : 'Enabled'

module monitoring 'modules/monitoring.bicep' = {
  name: 'monitoring'
  params: { prefix: prefix, location: location, tags: tags }
}

module identity 'modules/identity.bicep' = {
  name: 'identity'
  params: { prefix: prefix, location: location, tags: tags }
}

module network 'modules/network.bicep' = {
  name: 'network'
  params: { prefix: prefix, location: location, tags: tags }
}

module keyVault 'modules/keyvault.bicep' = {
  name: 'keyvault'
  params: {
    prefix: prefix
    location: location
    tags: tags
    adminObjectId: deployerObjectId
    apiPrincipalId: identity.outputs.api.principalId
    workerPrincipalId: identity.outputs.worker.principalId
    migratorPrincipalId: identity.outputs.migrator.principalId
    publicNetworkAccess: closed
    privateEndpointSubnetId: privateNetworking ? network.outputs.privateEndpointSubnetId : ''
    vaultZoneId: network.outputs.vaultZoneId
    monitoringWorkspaceId: monitoring.outputs.workspaceId
    lockResource: lockResources
  }
}

module storage 'modules/storage.bicep' = {
  name: 'storage'
  params: {
    prefix: prefix
    location: location
    tags: tags
    publicNetworkAccess: closed
    privateEndpointSubnetId: privateNetworking ? network.outputs.privateEndpointSubnetId : ''
    blobZoneId: network.outputs.blobZoneId
    apiPrincipalId: identity.outputs.api.principalId
    workerPrincipalId: identity.outputs.worker.principalId
    migratorPrincipalId: identity.outputs.migrator.principalId
    monitoringWorkspaceId: monitoring.outputs.workspaceId
    lockResource: lockResources
  }
}

module anchors 'modules/anchors.bicep' = {
  name: 'anchors'
  params: {
    prefix: prefix
    location: location
    tags: tags
    publicNetworkAccess: closed
    privateEndpointSubnetId: privateNetworking ? network.outputs.privateEndpointSubnetId : ''
    blobZoneId: network.outputs.blobZoneId
    apiPrincipalId: identity.outputs.api.principalId
    workerPrincipalId: identity.outputs.worker.principalId
    migratorPrincipalId: identity.outputs.migrator.principalId
    monitoringWorkspaceId: monitoring.outputs.workspaceId
    lockResource: lockResources
  }
}

module sql 'modules/sql.bicep' = {
  name: 'sql'
  params: {
    prefix: prefix
    location: location
    tags: tags
    adminObjectId: sqlAdminObjectId
    adminName: sqlAdminName
    skuName: sqlSkuName
    skuTier: sqlSkuTier
    capacity: sqlCapacity
    maxSizeGb: sqlMaxSizeGb
    zoneRedundant: zoneRedundant
    publicNetworkAccess: closed
    privateEndpointSubnetId: privateNetworking ? network.outputs.privateEndpointSubnetId : ''
    sqlZoneId: network.outputs.sqlZoneId
    monitoringWorkspaceId: monitoring.outputs.workspaceId
    lockResource: lockResources
  }
}

module registry 'modules/registry.bicep' = {
  name: 'registry'
  params: {
    prefix: prefix
    location: location
    tags: tags
    pullPrincipalIds: [
      identity.outputs.api.principalId
      identity.outputs.web.principalId
      identity.outputs.worker.principalId
      identity.outputs.migrator.principalId
    ]
    // The pipeline pushes images from a GitHub-hosted runner, so the registry stays reachable; access is Entra-only (no admin user, no anonymous pull).
    publicNetworkAccess: 'Enabled'
    monitoringWorkspaceId: monitoring.outputs.workspaceId
  }
}

module environment 'modules/containerenv.bicep' = {
  name: 'containerenv'
  params: {
    prefix: prefix
    location: location
    tags: tags
    subnetId: network.outputs.appsSubnetId
    monitoringWorkspaceName: monitoring.outputs.workspaceName
    zoneRedundant: zoneRedundant
  }
}

var webHost = '${prefix}-web.${environment.outputs.defaultDomain}'
var effectivePublicHost = empty(publicHost) ? webHost : publicHost

module apps 'modules/containerapps.bicep' = if (deployApps) {
  name: 'apps-${revisionSuffix}'
  params: {
    prefix: prefix
    location: location
    tags: tags
    environmentName: environment.outputs.name
    appInsightsConnectionString: monitoring.outputs.appInsightsConnectionString
    registryLoginServer: registry.outputs.loginServer
    apiImage: '${registry.outputs.loginServer}/vpms-api:${imageTag}'
    workerImage: '${registry.outputs.loginServer}/vpms-worker:${imageTag}'
    webImage: '${registry.outputs.loginServer}/vpms-web:${imageTag}'
    migratorImage: '${registry.outputs.loginServer}/vpms-migrator:${imageTag}'
    revisionSuffix: revisionSuffix
    apiTraffic: apiTraffic
    webTraffic: webTraffic
    api: identity.outputs.api
    web: identity.outputs.web
    worker: identity.outputs.worker
    migrator: identity.outputs.migrator
    sqlFqdn: sql.outputs.fqdn
    databaseName: sql.outputs.databaseName
    keyVaultUri: keyVault.outputs.uri
    kekKeyId: keyVault.outputs.kekKeyId
    dataProtectionKeyId: keyVault.outputs.dataProtectionKeyId
    storageBlobUrl: storage.outputs.blobUrl
    anchorBlobUrl: anchors.outputs.blobUrl
    publicHost: effectivePublicHost
    trustedProxyNetwork: network.outputs.appsSubnetPrefix
    entraPrincipals: '${identity.outputs.api.name},${identity.outputs.worker.name}'
    frontDoorId: frontDoorId
    apiMinReplicas: apiMinReplicas
    webMinReplicas: webMinReplicas
  }
}

module edge 'modules/frontdoor.bicep' = if (enableFrontDoor) {
  name: 'frontdoor'
  params: {
    prefix: prefix
    tags: tags
    originHost: webHost
    monitoringWorkspaceId: monitoring.outputs.workspaceId
  }
}

module alerts 'modules/alerts.bicep' = {
  name: 'alerts'
  params: {
    prefix: prefix
    location: location
    tags: tags
    workspaceId: monitoring.outputs.workspaceId
    sqlServerResourceId: resourceId('Microsoft.Sql/servers', sql.outputs.serverName)
    emailReceivers: alertEmails
    webhookReceivers: alertWebhooks
  }
}

output keyVaultName string = keyVault.outputs.name
output sqlServerName string = sql.outputs.serverName
output sqlFqdn string = sql.outputs.fqdn
output registryName string = registry.outputs.name
output registryLoginServer string = registry.outputs.loginServer
output environmentName string = environment.outputs.name
output webHost string = webHost
output publicHost string = effectivePublicHost
output frontDoorHost string = enableFrontDoor ? edge.outputs.hostName : ''
output frontDoorId string = enableFrontDoor ? edge.outputs.frontDoorId : ''
output apiIdentityName string = identity.outputs.api.name
output migratorIdentityName string = identity.outputs.migrator.name
output anchorAccountName string = anchors.outputs.name
