targetScope = 'resourceGroup'

@description('Short environment name, e.g. dev, test, prod')
param environmentName string = 'dev'
param location string = resourceGroup().location
@description('Entra object id granted Key Vault secret access (deployment identity)')
param keyVaultAdminObjectId string

var prefix = 'vpms-${environmentName}'

module monitoring 'modules/monitoring.bicep' = {
  name: 'monitoring'
  params: { prefix: prefix, location: location }
}

module keyVault 'modules/keyvault.bicep' = {
  name: 'keyvault'
  params: { prefix: prefix, location: location, adminObjectId: keyVaultAdminObjectId }
}

module storage 'modules/storage.bicep' = {
  name: 'storage'
  params: { prefix: replace(prefix, '-', ''), location: location }
}

module servicebus 'modules/servicebus.bicep' = {
  name: 'servicebus'
  params: { prefix: prefix, location: location }
}

output keyVaultName string = keyVault.outputs.name
