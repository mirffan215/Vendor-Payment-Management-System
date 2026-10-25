using 'main.bicep'

// Production: private data services, zone-redundant database and environment, Front Door + WAF, deletion locks, two or more replicas.
param environmentName = 'prod'
param deployerObjectId = readEnvironmentVariable('VPMS_DEPLOYER_OBJECT_ID', '')
param sqlAdminObjectId = readEnvironmentVariable('VPMS_SQL_ADMIN_OBJECT_ID', '')
param sqlAdminName = readEnvironmentVariable('VPMS_SQL_ADMIN_NAME', 'vpms-prod-sql-admins')
param publicHost = readEnvironmentVariable('VPMS_PUBLIC_HOST', '')
param privateNetworking = true
param enableFrontDoor = true
param zoneRedundant = true
param lockResources = true
param sqlSkuName = 'GP_Gen5'
param sqlSkuTier = 'GeneralPurpose'
param sqlCapacity = 4
param sqlMaxSizeGb = 256
param apiMinReplicas = 2
param webMinReplicas = 2

// Release inputs, supplied by .github/workflows/deploy.yml (see docs/operations/deployment.md).
param deployApps = toLower(readEnvironmentVariable('VPMS_DEPLOY_APPS', 'false')) == 'true'
param imageTag = readEnvironmentVariable('VPMS_IMAGE_TAG', 'none')
param revisionSuffix = readEnvironmentVariable('VPMS_REVISION_SUFFIX', 'initial')
param apiTraffic = json(readEnvironmentVariable('VPMS_API_TRAFFIC', '[{"latestRevision":true,"weight":100}]'))
param webTraffic = json(readEnvironmentVariable('VPMS_WEB_TRAFFIC', '[{"latestRevision":true,"weight":100}]'))
param frontDoorId = readEnvironmentVariable('VPMS_FRONT_DOOR_ID', '')
param alertEmails = json(readEnvironmentVariable('VPMS_ALERT_EMAILS', '[]'))
