using 'main.bicep'

// The live names. The Po standard would name the App Service app-poredomedia; these resources predate it and keep
// their names (renaming means recreating them and moving the data; see AGENTS.md).
param location = 'westus3'
param planName = 'asp-PoRedoMedia-f1'
param planSku = 'F1'
param appName = 'app-poredomedia2'
param storageName = 'stporedomedia2'
param keyVaultUri = 'https://kv-poshared2.vault.azure.net/'
