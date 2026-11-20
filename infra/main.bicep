// PoRedoMedia's Azure resources, as they run today (read from the live resource group PoRedoMedia, 2026-11-20).
// Scope: resource group. Preview with `az deployment group what-if`; CI never deploys this file.
// Not here on purpose: Key Vault kv-poshared2 and its access policy for the app's identity live in the shared
// resource group PoShared (scripts/ensure-azure-access.ps1 grants them), and the app's secrets are in that vault.

@description('Region of every resource in this group.')
param location string = resourceGroup().location

@description('App Service plan (Linux).')
param planName string

@description('App Service plan SKU. F1 (Free) has no Always On and 1 GB of disk.')
param planSku string = 'F1'

@description('App Service that hosts the API and the Blazor client.')
param appName string

@description('Storage account: Library blobs, runs, settings and tokens (Tables).')
param storageName string

@description('Key Vault the app reads its PoRedoMedia--* secrets from at startup.')
param keyVaultUri string

resource plan 'Microsoft.Web/serverfarms@2024-04-01' = {
  name: planName
  location: location
  kind: 'linux'
  sku: {
    name: planSku
  }
  properties: {
    reserved: true // Linux
  }
}

resource app 'Microsoft.Web/sites@2024-04-01' = {
  name: appName
  location: location
  kind: 'app,linux'
  identity: {
    type: 'SystemAssigned' // reads kv-poshared2
  }
  properties: {
    serverFarmId: plan.id
    httpsOnly: true
    clientAffinityEnabled: true
    publicNetworkAccess: 'Enabled'
    siteConfig: {
      linuxFxVersion: 'DOTNETCORE|10.0'
      appCommandLine: 'dotnet PoRedoMedia.Api.dll'
      alwaysOn: false // not available on F1
      webSocketsEnabled: true // SignalR run progress (/hubs/run)
      http20Enabled: false
      ftpsState: 'Disabled'
      minTlsVersion: '1.2'
      use32BitWorkerProcess: true
      appSettings: [
        {
          name: 'KeyVault__Uri'
          value: keyVaultUri
        }
      ]
    }
  }
}

resource storage 'Microsoft.Storage/storageAccounts@2023-05-01' = {
  name: storageName
  location: location
  kind: 'StorageV2'
  sku: {
    name: 'Standard_LRS'
  }
  properties: {
    accessTier: 'Hot'
    minimumTlsVersion: 'TLS1_2'
    supportsHttpsTrafficOnly: true
    allowBlobPublicAccess: false
  }
}

// Browsers upload straight to blob storage with a SAS link (PUT) and read results back (GET), so blobs allow CORS.
resource blobService 'Microsoft.Storage/storageAccounts/blobServices@2023-05-01' = {
  parent: storage
  name: 'default'
  properties: {
    deleteRetentionPolicy: {
      enabled: false
      allowPermanentDelete: false
    }
    cors: {
      corsRules: [
        {
          allowedOrigins: ['*']
          allowedMethods: ['PUT', 'GET', 'HEAD', 'OPTIONS']
          allowedHeaders: ['*']
          exposedHeaders: ['ETag']
          maxAgeInSeconds: 3600
        }
      ]
    }
  }
}

output appUrl string = 'https://${app.properties.defaultHostName}'
output appPrincipalId string = app.identity.principalId
