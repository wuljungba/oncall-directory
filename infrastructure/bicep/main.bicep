param location string = 'eastus2'
param environmentName string = 'dev'
@secure()
param sqlAdminPassword string
param sqlAdminLogin string = 'oncalladmin'
param entraTenantId string
param entraClientId string
param entraDomain string
param corsOrigin string = ''

var resourceGroupName = 'rg-oncall-${environmentName}'
var appName = 'app-oncall-${environmentName}'
var sqlServerName = 'sql-oncall-${environmentName}'
var sqlDbName = 'sqldb-oncall-${environmentName}'
var kvName = 'kv-${take(environmentName, 4)}-${uniqueString(subscription().subscriptionId)}'
var aiName = 'ai-oncall-${environmentName}'
var logName = 'log-oncall-${environmentName}'
var stName = 'stoncall${environmentName}'
var redisName = 'redis-oncall-${environmentName}'
var connectionString = 'Server=tcp:${sqlServer.properties.fullyQualifiedDomainName},1433;Database=${sqlDbName};User ID=${sqlAdminLogin};Password=${sqlAdminPassword};TrustServerCertificate=False;Encrypt=True;'
var defaultCorsOrigin = !empty(corsOrigin) ? corsOrigin : 'https://${appName}.azurewebsites.net'

// ── Application Insights + Log Analytics ──
resource logAnalytics 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: logName
  location: location
  properties: {
    sku: { name: 'PerGB2018' }
    retentionInDays: 90
  }
}

resource appInsights 'Microsoft.Insights/components@2020-02-02' = {
  name: aiName
  location: location
  kind: 'web'
  properties: {
    Application_Type: 'web'
    WorkspaceResourceId: logAnalytics.id
  }
}

// ── Azure SQL Database (General Purpose Serverless) ──
resource sqlServer 'Microsoft.Sql/servers@2023-08-01-preview' = {
  name: sqlServerName
  location: location
  properties: {
    administratorLogin: sqlAdminLogin
    administratorLoginPassword: sqlAdminPassword
    minimalTlsVersion: '1.2'
  }
}

resource allowAzureServices 'Microsoft.Sql/servers/firewallRules@2023-08-01-preview' = {
  parent: sqlServer
  name: 'AllowAzureServices'
  properties: {
    startIpAddress: '0.0.0.0'
    endIpAddress: '0.0.0.0'
  }
}

resource sqlDb 'Microsoft.Sql/servers/databases@2023-08-01-preview' = {
  parent: sqlServer
  name: sqlDbName
  location: location
  sku: {
    name: 'GP_S_Gen5'
    tier: 'GeneralPurpose'
    capacity: 2
  }
  properties: {
    collation: 'SQL_Latin1_General_CP1_CI_AS'
    maxSizeBytes: 536870912000 // 500 GB
    autoPauseDelay: environmentName == 'production' ? -1 : 60
    requestedBackupStorageRedundancy: 'Geo'
  }
}

// ── Key Vault ──
resource keyVault 'Microsoft.KeyVault/vaults@2023-07-01' = {
  name: kvName
  location: location
  properties: {
    sku: {
      family: 'A'
      name: 'standard'
    }
    tenantId: subscription().tenantId
    enableRbacAuthorization: true
    softDeleteRetentionInDays: 7
  }
}

resource sqlConnectionStringSecret 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = {
  parent: keyVault
  name: 'SqlConnectionString'
  properties: {
    value: connectionString
  }
}

// ── Storage Account (for CSV imports, audit archives, compliance reports) ──
resource storageAccount 'Microsoft.Storage/storageAccounts@2023-01-01' = {
  name: stName
  location: location
  kind: 'StorageV2'
  sku: { name: 'Standard_GRS' }
  properties: {
    allowBlobPublicAccess: false
    minimumTlsVersion: 'TLS1_2'
    supportsHttpsTrafficOnly: true
    defaultToOAuthAuthentication: true
  }
}

resource blobService 'Microsoft.Storage/storageAccounts/blobServices@2023-01-01' = {
  parent: storageAccount
  name: 'default'
  properties: {
    deleteRetentionPolicy: { enabled: true, days: 7 }
  }
}

resource importFilesContainer 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-01-01' = {
  parent: blobService
  name: 'import-files'
}

resource auditArchiveContainer 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-01-01' = {
  parent: blobService
  name: 'audit-archive'
}

resource complianceReportsContainer 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-01-01' = {
  parent: blobService
  name: 'compliance-reports'
}

// ── Redis Cache (for session/query caching) ──
resource redisCache 'Microsoft.Cache/redis@2023-08-01' = {
  name: redisName
  location: location
  properties: {
    sku: { name: 'Standard', family: 'C', capacity: environmentName == 'production' ? 1 : 0 }
    enableNonSslPort: false
    minimumTlsVersion: '1.2'
    redisConfiguration: {
      'maxmemory-reserved': '100'
      'maxfragmentationmemory-reserved': '50'
    }
  }
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
        { name: 'ConnectionStrings__DefaultConnection', value: '@Microsoft.KeyVault(SecretUri=https://${kvName}.vault.azure.net/secrets/SqlConnectionString)' }
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
        { name: 'ConnectionStrings__DefaultConnection', value: '@Microsoft.KeyVault(SecretUri=https://${kvName}.vault.azure.net/secrets/SqlConnectionString)' }
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

// ── Key Vault RBAC: grant the web app + staging slot identities the
//    "Key Vault Secrets User" role so the ConnectionStrings__DefaultConnection
//    Key Vault reference resolves at runtime. The vault enables RBAC authorization
//    (enableRbacAuthorization: true), which ignores classic access policies, so a
//    role assignment is required (previously a classic access policy was declared
//    here but silently had no effect under RBAC).
resource kvSecretsUserApp 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: keyVault
  name: guid(keyVault.id, webApp.id, 'kvsecrets-user')
  properties: {
    roleDefinitionId: '/providers/Microsoft.Authorization/roleDefinitions/4633458b-17de-408a-b874-0445c86b69e6'
    principalId: webApp.identity.principalId
  }
}

resource kvSecretsUserSlot 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: keyVault
  name: guid(keyVault.id, stagingSlot.id, 'kvsecrets-user')
  properties: {
    roleDefinitionId: '/providers/Microsoft.Authorization/roleDefinitions/4633458b-17de-408a-b874-0445c86b69e6'
    principalId: stagingSlot.identity.principalId
  }
}

// ── Storage RBAC for Web App Managed Identity ──
resource storageRbac 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(storageAccount.id, resourceGroupName, 'StorageBlobDataContributor')
  scope: storageAccount
  properties: {
    principalId: webApp.identity.principalId
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'ba92f5b4-2d11-453d-a403-e96b0029c9fe') // Storage Blob Data Contributor
    principalType: 'ServicePrincipal'
  }
}

// ── Outputs ──
output appUrl string = 'https://${appName}.azurewebsites.net'
output sqlServerFqdn string = sqlServer.properties.fullyQualifiedDomainName
output keyVaultName string = kvName
output storageAccountName string = stName
output redisHostName string = redisCache.properties.hostName
output appInsightsConnectionString string = appInsights.properties.ConnectionString