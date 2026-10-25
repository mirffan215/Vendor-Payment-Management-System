param prefix string
param location string
param tags object

// One identity per workload, so each gets only the access it needs (least privilege) and a compromise of one does not open the others.
resource api 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = { name: '${prefix}-id-api', location: location, tags: tags }
resource web 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = { name: '${prefix}-id-web', location: location, tags: tags }
resource worker 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = { name: '${prefix}-id-worker', location: location, tags: tags }
resource migrator 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = { name: '${prefix}-id-migrator', location: location, tags: tags }

output api object = { id: api.id, principalId: api.properties.principalId, clientId: api.properties.clientId, name: api.name }
output worker object = { id: worker.id, principalId: worker.properties.principalId, clientId: worker.properties.clientId, name: worker.name }
output migrator object = { id: migrator.id, principalId: migrator.properties.principalId, clientId: migrator.properties.clientId, name: migrator.name }
output web object = { id: web.id, principalId: web.properties.principalId, clientId: web.properties.clientId, name: web.name }
