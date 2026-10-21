param prefix string
param location string

resource sa 'Microsoft.Storage/storageAccounts@2023-05-01' = {
  name: take('${prefix}st', 24)
  location: location
  sku: { name: 'Standard_GRS' }
  kind: 'StorageV2'
  properties: {
    minimumTlsVersion: 'TLS1_2'
    allowBlobPublicAccess: false
    supportsHttpsTrafficOnly: true
  }
}

output name string = sa.name
