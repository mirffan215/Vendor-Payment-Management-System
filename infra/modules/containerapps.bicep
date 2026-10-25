// Hosting: one Container Apps environment in the VNet; api and web in Multiple-revision mode so a new version can be started at 0% traffic,
// checked, and only then given the traffic (blue/green; ADR-0025). Secrets are never environment variables: the apps read Key Vault with their
// managed identity (KeyVault__Uri) and SQL/Storage with the same identity.
param prefix string
param location string
param tags object
param environmentName string
param appInsightsConnectionString string

param registryLoginServer string
param apiImage string
param workerImage string
param webImage string
param migratorImage string
param clamavImage string = 'docker.io/clamav/clamav:1.4'

@description('Suffix of the new revision, e.g. the first 12 characters of the commit. Must be lowercase letters, digits or hyphen.')
param revisionSuffix string
@description('Traffic split for api and web. The deployment workflow sends the previous revision at 100 and latest at 0 (blue/green), then moves it after the health gate.')
param apiTraffic array = [{ latestRevision: true, weight: 100 }]
param webTraffic array = [{ latestRevision: true, weight: 100 }]

param api object
param worker object
param web object
param migrator object
param sqlFqdn string
param databaseName string
param keyVaultUri string
param kekKeyId string
param dataProtectionKeyId string
param storageBlobUrl string
param anchorBlobUrl string
param publicHost string
@description('Address range of the apps subnet: the ingress proxies live there and are the only source of X-Forwarded-* headers that the API trusts.')
param trustedProxyNetwork string
param entraPrincipals string
param frontDoorId string = ''
param apiMinReplicas int = 2
param apiMaxReplicas int = 10
param webMinReplicas int = 2

resource env 'Microsoft.App/managedEnvironments@2024-03-01' existing = {
  name: environmentName
}

var sqlBase = 'Server=tcp:${sqlFqdn},1433;Database=${databaseName};Authentication=Active Directory Managed Identity;Encrypt=True;Connection Timeout=30;User Id='

var common = [
  { name: 'DOTNET_ENVIRONMENT', value: 'Production' }
  { name: 'ASPNETCORE_ENVIRONMENT', value: 'Production' }
  { name: 'KeyVault__Uri', value: keyVaultUri }
  { name: 'Encryption__Provider', value: 'KeyVault' }
  { name: 'Encryption__KeyVaultKeyId', value: kekKeyId }
  { name: 'Storage__Provider', value: 'Azure' }
  { name: 'Storage__Azure__AccountUrl', value: storageBlobUrl }
  { name: 'Storage__Azure__AnchorAccountUrl', value: anchorBlobUrl }
  { name: 'Storage__AnchorContainer', value: 'audit-anchors' }
  { name: 'Audit__AnchorSink', value: 'Blob' }
  { name: 'Documents__Scanner', value: 'ClamAv' }
  { name: 'Documents__ClamAv__Host', value: '${prefix}-clamav' }
  { name: 'Documents__ClamAv__Port', value: '3310' }
  { name: 'APPLICATIONINSIGHTS_CONNECTION_STRING', value: appInsightsConnectionString }
]

var apiEnv = concat(common, [
  { name: 'AZURE_CLIENT_ID', value: api.clientId }
  { name: 'ConnectionStrings__Default', value: '${sqlBase}${api.clientId}' }
  { name: 'DataProtection__Provider', value: 'AzureBlob' }
  { name: 'DataProtection__BlobUri', value: '${storageBlobUrl}dataprotection/keys.xml' }
  { name: 'DataProtection__KeyVaultKeyId', value: dataProtectionKeyId }
  { name: 'AllowedHosts', value: publicHost }
  { name: 'App__BaseUrl', value: 'https://${publicHost}' }
  { name: 'ForwardedHeaders__KnownNetworks__0', value: trustedProxyNetwork }
  { name: 'ASPNETCORE_URLS', value: 'http://+:8080' }
  { name: 'ForwardedHeaders__ForwardLimit', value: '4' }
])

var workerEnv = concat(common, [
  { name: 'AZURE_CLIENT_ID', value: worker.clientId }
  { name: 'ConnectionStrings__Default', value: '${sqlBase}${worker.clientId}' }
  { name: 'App__BaseUrl', value: 'https://${publicHost}' }
])


resource clamav 'Microsoft.App/containerApps@2024-03-01' = {
  name: '${prefix}-clamav'
  location: location
  tags: tags
  properties: {
    managedEnvironmentId: env.id
    workloadProfileName: 'Consumption'
    configuration: {
      activeRevisionsMode: 'Single'
      // Reachable by the other apps in the environment only, on the clamd port.
      ingress: { external: false, transport: 'tcp', targetPort: 3310, exposedPort: 3310 }
    }
    template: {
      containers: [{
        name: 'clamav'
        image: clamavImage
        resources: { cpu: json('2'), memory: '4Gi' }
        probes: [{ type: 'Startup', tcpSocket: { port: 3310 }, initialDelaySeconds: 30, periodSeconds: 15, failureThreshold: 20 }]
      }]
      scale: { minReplicas: 1, maxReplicas: 2 }
    }
  }
}

resource apiApp 'Microsoft.App/containerApps@2024-03-01' = {
  name: '${prefix}-api'
  location: location
  tags: tags
  identity: { type: 'UserAssigned', userAssignedIdentities: { '${api.id}': {} } }
  properties: {
    managedEnvironmentId: env.id
    workloadProfileName: 'Consumption'
    configuration: {
      activeRevisionsMode: 'Multiple'
      // Internal: the browser reaches the API through the web app (same origin), never directly.
      ingress: { external: false, targetPort: 8080, transport: 'http', allowInsecure: true, traffic: apiTraffic }
      registries: [{ server: registryLoginServer, identity: api.id }]
    }
    template: {
      revisionSuffix: revisionSuffix
      containers: [{
        name: 'api'
        image: apiImage
        env: apiEnv
        resources: { cpu: json('1'), memory: '2Gi' }
        probes: [
          { type: 'Startup', httpGet: { path: '/health/live', port: 8080 }, initialDelaySeconds: 5, periodSeconds: 5, failureThreshold: 30 }
          { type: 'Liveness', httpGet: { path: '/health/live', port: 8080 }, periodSeconds: 15, failureThreshold: 3 }
          { type: 'Readiness', httpGet: { path: '/health/ready', port: 8080 }, periodSeconds: 10, failureThreshold: 3 }
        ]
      }]
      scale: {
        minReplicas: apiMinReplicas
        maxReplicas: apiMaxReplicas
        rules: [{ name: 'http', http: { metadata: { concurrentRequests: '60' } } }]
      }
    }
  }
}

resource webApp 'Microsoft.App/containerApps@2024-03-01' = {
  name: '${prefix}-web'
  location: location
  tags: tags
  identity: { type: 'UserAssigned', userAssignedIdentities: { '${web.id}': {} } }
  properties: {
    managedEnvironmentId: env.id
    workloadProfileName: 'Consumption'
    configuration: {
      activeRevisionsMode: 'Multiple'
      ingress: { external: true, targetPort: 8080, transport: 'http', allowInsecure: false, traffic: webTraffic }
      registries: [{ server: registryLoginServer, identity: web.id }]
    }
    template: {
      revisionSuffix: revisionSuffix
      containers: [{
        name: 'web'
        image: webImage
        env: [
          { name: 'API_UPSTREAM', value: 'http://${apiApp.name}' }
          { name: 'FRONT_DOOR_ID', value: frontDoorId }
        ]
        resources: { cpu: json('0.5'), memory: '1Gi' }
        probes: [
          { type: 'Liveness', httpGet: { path: '/healthz', port: 8080 }, periodSeconds: 15 }
          { type: 'Readiness', httpGet: { path: '/healthz', port: 8080 }, periodSeconds: 10 }
        ]
      }]
      scale: { minReplicas: webMinReplicas, maxReplicas: 6, rules: [{ name: 'http', http: { metadata: { concurrentRequests: '100' } } }] }
    }
  }
}

resource workerApp 'Microsoft.App/containerApps@2024-03-01' = {
  name: '${prefix}-worker'
  location: location
  tags: tags
  identity: { type: 'UserAssigned', userAssignedIdentities: { '${worker.id}': {} } }
  properties: {
    managedEnvironmentId: env.id
    workloadProfileName: 'Consumption'
    configuration: {
      activeRevisionsMode: 'Single'
      registries: [{ server: registryLoginServer, identity: worker.id }]
    }
    template: {
      revisionSuffix: revisionSuffix
      containers: [{
        name: 'worker'
        image: workerImage
        env: workerEnv
        resources: { cpu: json('1'), memory: '2Gi' }
      }]
      // The jobs lease their work, but one replica keeps the ordering of per-tenant queues simple; scale out after measuring.
      scale: { minReplicas: 1, maxReplicas: 1 }
    }
  }
}

// Runs before each release (the pipeline starts it and waits): applies migrations, then makes sure the identities are database users.
resource migratorJob 'Microsoft.App/jobs@2024-03-01' = {
  name: '${prefix}-migrator'
  location: location
  tags: tags
  identity: { type: 'UserAssigned', userAssignedIdentities: { '${migrator.id}': {} } }
  properties: {
    environmentId: env.id
    workloadProfileName: 'Consumption'
    configuration: {
      triggerType: 'Manual'
      replicaTimeout: 1800
      replicaRetryLimit: 1
      manualTriggerConfig: { parallelism: 1, replicaCompletionCount: 1 }
      registries: [{ server: registryLoginServer, identity: migrator.id }]
    }
    template: {
      containers: [{
        name: 'migrator'
        image: migratorImage
        env: [
          { name: 'AZURE_CLIENT_ID', value: migrator.clientId }
          { name: 'ConnectionStrings__Migrator', value: '${sqlBase}${migrator.clientId}' }
          { name: 'VPMS_ENTRA_PRINCIPALS', value: entraPrincipals }
          { name: 'KeyVault__Uri', value: keyVaultUri }
          { name: 'Encryption__Provider', value: 'KeyVault' }
          { name: 'Encryption__KeyVaultKeyId', value: kekKeyId }
          { name: 'Storage__Provider', value: 'Azure' }
          { name: 'Storage__Azure__AccountUrl', value: storageBlobUrl }
          { name: 'Storage__Azure__AnchorAccountUrl', value: anchorBlobUrl }
          { name: 'Storage__AnchorContainer', value: 'audit-anchors' }
          { name: 'Audit__AnchorSink', value: 'Blob' }
        ]
        resources: { cpu: json('0.5'), memory: '1Gi' }
      }]
    }
  }
}


output webFqdn string = webApp.properties.configuration.ingress.fqdn
output apiName string = apiApp.name
output webName string = webApp.name
output workerName string = workerApp.name
output migratorJobName string = migratorJob.name
