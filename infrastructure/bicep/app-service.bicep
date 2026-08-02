// Incremental deployment: App Service Plan + Web App + staging slot ONLY.
//
// Used when main.bicep's Microsoft.Web resources are missing (e.g. the RG was
// deployed from an earlier/partial run). References the existing SQL/Redis/
// Storage/App Insights/Key Vault resources WITHOUT modifying them, so it does
// not need the SQL admin password.
//
// Deploy with:
//   az deployment group create --resource-group rg-oncall-production \
//     --template-file infrastructure/bicep/app-service.bicep \
//     --parameters environmentName=production entraTenantId=... entraClientId=... \
//                  entraDomain=... corsOrigin=...

param location string = 'westus3'
param environmentName string = 'production'
param entraTenantId string
param entraClientId string
param entraDomain string
param corsOrigin string = ''
param kvName string = 'kv-prod-lfwnuoyu5blao'

var appName = 'app-oncall-${environmentName}'
var aiName = 'ai-oncall-${environmentName}'
var stName = 'stoncall${environmentName}'
var redisName = 'redis-oncall-${environmentName}'
var defaultCorsOrigin = !empty(corsOrigin) ? corsOrigin : 'https://${appName}.azurewebsites.net'

// ── References to existing resources (read-only, not modified) ──
resource appInsights 'Microsoft.Insights/components@2020-02-02' existing = {
  name: aiName
}
resource storageAccount 'Microsoft.Storage/storageAccounts@2023-01-01' existing = {
  name: stName
}
resource redisCache 'Microsoft.Cache/redis@2023-08-01' existing = {
  name: redisName
}

// ── App Service Plan + Web App (serves both API and frontend static files) ──
resource appPlan 'Microsoft.Web/serverfarms@2023-12-01' = {
  name: 'plan-oncall-${environmentName}'
  location: location
  sku: {
    name: 'S1'
    tier: 'Standard'
    capacity: 1
  }
}

resource webApp 'Microsoft.Web/sites@2023-12-01' = {
  name: appName
  location: location
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    serverFarmId: appPlan.id
    httpsOnly: true
    siteConfig: {
      netFrameworkVersion: 'v8.0'
      alwaysOn: true
      minTlsVersion: '1.2'
      appSettings: [
        { name: 'ConnectionStrings__DefaultConnection', value: '@Microsoft.KeyVault(SecretUri=https://${kvName}.vault.azure.net/secrets/SqlConnectionString/)' }
        { name: 'AzureAd__Instance', value: replace(environment().authentication.loginEndpoint, '/$', '') }
        { name: 'AzureAd__Domain', value: entraDomain }
        { name: 'AzureAd__TenantId', value: entraTenantId }
        { name: 'AzureAd__ClientId', value: entraClientId }
        { name: 'Cors__Origin', value: defaultCorsOrigin }
        { name: 'ApplicationInsights__ConnectionString', value: appInsights.properties.ConnectionString }
        { name: 'Redis__ConnectionString', value: redisCache.properties.hostName }
        { name: 'Storage__ConnectionString', value: storageAccount.properties.primaryEndpoints.blob }
        { name: 'WEBSITE_RUN_FROM_PACKAGE', value: '1' }
        { name: 'DevAuth__Enabled', value: 'false' }
      ]
    }
  }
}

// ── Staging Slot (for zero-downtime deployments) ──
resource stagingSlot 'Microsoft.Web/sites/slots@2023-12-01' = {
  parent: webApp
  name: 'staging'
  location: location
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    serverFarmId: appPlan.id
    httpsOnly: true
    siteConfig: {
      netFrameworkVersion: 'v8.0'
      alwaysOn: true
      minTlsVersion: '1.2'
      appSettings: [
        { name: 'ConnectionStrings__DefaultConnection', value: '@Microsoft.KeyVault(SecretUri=https://${kvName}.vault.azure.net/secrets/SqlConnectionString/)' }
        { name: 'AzureAd__Instance', value: replace(environment().authentication.loginEndpoint, '/$', '') }
        { name: 'AzureAd__Domain', value: entraDomain }
        { name: 'AzureAd__TenantId', value: entraTenantId }
        { name: 'AzureAd__ClientId', value: entraClientId }
        { name: 'Cors__Origin', value: defaultCorsOrigin }
        { name: 'ApplicationInsights__ConnectionString', value: appInsights.properties.ConnectionString }
        { name: 'Redis__ConnectionString', value: redisCache.properties.hostName }
        { name: 'Storage__ConnectionString', value: storageAccount.properties.primaryEndpoints.blob }
        { name: 'WEBSITE_RUN_FROM_PACKAGE', value: '1' }
        { name: 'DevAuth__Enabled', value: 'false' }
      ]
    }
  }
}
