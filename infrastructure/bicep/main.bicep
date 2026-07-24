param location string = 'eastus'
param environmentName string = 'dev'
@secure()
param sqlAdminPassword string
param sqlAdminLogin string = 'oncalladmin'
param entraTenantId string
param entraClientId string
param entraDomain string

var resourceGroupName = 'rg-oncall-${environmentName}'
var appName = 'app-oncall-${environmentName}'
var sqlServerName = 'sql-oncall-${environmentName}'
var sqlDbName = 'sqldb-oncall-${environmentName}'
var kvName = 'kv-oncall-${environmentName}'
var aiName = 'ai-oncall-${environmentName}'
var logName = 'log-oncall-${environmentName}'
var connectionString = 'Server=tcp:${sqlServer.properties.fullyQualifiedDomainName},1433;Database=${sqlDbName};User ID=${sqlAdminLogin};Password=${sqlAdminPassword};TrustServerCertificate=False;Encrypt=True;'

// ── Resource Group ──
resource rg 'Microsoft.Resources/resourceGroups@2023-07-01' = {
  name: resourceGroupName
  location: location
}

// ── Application Insights ──
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
  dependsOn: [logAnalytics]
}

// ── Azure SQL Database ──
resource sqlServer 'Microsoft.Sql/servers@2023-08-01-preview' = {
  name: sqlServerName
  location: location
  properties: {
    administratorLogin: sqlAdminLogin
    administratorLoginPassword: sqlAdminPassword
    minimalTlsVersion: '1.2'
    publicNetworkAccess: 'Disabled'
  }
}

resource sqlFirewallRule 'Microsoft.Sql/servers/firewallRules@2023-08-01-preview' = {
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
    name: 'S2'
    tier: 'Standard'
  }
  properties: {
    collation: 'SQL_Latin1_General_CP1_CI_AS'
    maxSizeBytes: 268435456000 // 250 GB
  }
}

// ── Key Vault ──
resource keyVault 'Microsoft.KeyVault/vaults@2023-07-01' = {
  name: kvName
  location: location
  properties: {
    sku: {
      family: 'A'
      name: 'Standard'
    }
    tenantId: subscription().tenantId
    enableRbacAuthorization: true
    softDeleteRetentionInDays: 7
  }
}

// ── Key Vault Secret: SQL Connection String ──
resource sqlConnectionStringSecret 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = {
  parent: keyVault
  name: 'SqlConnectionString'
  properties: {
    value: connectionString
  }
}

// ── App Service Plan + Web App ──
resource appPlan 'Microsoft.Web/serverfarms@2023-12-01' = {
  name: 'plan-oncall-${environmentName}'
  location: location
  sku: {
    name: 'P1v2'
    tier: 'PremiumV2'
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
        { name: 'AzureAd__Instance', value: 'https://login.microsoftonline.com/' }
        { name: 'AzureAd__Domain', value: entraDomain }
        { name: 'AzureAd__TenantId', value: entraTenantId }
        { name: 'AzureAd__ClientId', value: entraClientId }
        { name: 'Cors__Origin', value: 'https://${appName}.azurewebsites.net' }
        { name: 'ApplicationInsights__ConnectionString', value: appInsights.properties.ConnectionString }
        { name: 'WEBSITE_RUN_FROM_PACKAGE', value: '1' }
      ]
    }
  }
}

// ── Staging Slot (for zero-downtime deployments) ──
resource stagingSlot 'Microsoft.Web/sites/slots@2023-12-01' = {
  name: '${appName}/staging'
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
        { name: 'AzureAd__Instance', value: 'https://login.microsoftonline.com/' }
        { name: 'AzureAd__Domain', value: entraDomain }
        { name: 'AzureAd__TenantId', value: entraTenantId }
        { name: 'AzureAd__ClientId', value: entraClientId }
        { name: 'Cors__Origin', value: 'https://${appName}.azurewebsites.net' }
        { name: 'ApplicationInsights__ConnectionString', value: appInsights.properties.ConnectionString }
        { name: 'WEBSITE_RUN_FROM_PACKAGE', value: '1' }
      ]
    }
  }
}

// ── Key Vault Access Policy for Web App Managed Identity ──
resource kvAccessPolicy 'Microsoft.KeyVault/vaults/accessPolicies@2023-07-01' = {
  parent: keyVault
  name: 'add'
  properties: {
    accessPolicies: [
      {
        objectId: webApp.identity.principalId
        permissions: {
          secrets: ['get', 'list']
        }
        tenantId: subscription().tenantId
      }
    ]
  }
}

// ── Diagnostic Settings for Audit Logging ──
resource sqlDiagnostics 'Microsoft.Sql/servers/databases/providers/diagnosticSettings@2021-05-01-preview' = {
  name: '${sqlDbName}/Microsoft.Insights/sqlauditlogs'
  properties: {
    workspaceId: logAnalytics.id
    logs: [
      { category: 'SQLSecurityAuditEvents', enabled: true, retentionPolicy: { enabled: true, days: 365 } }
    ]
    metrics: [
      { category: 'AllMetrics', enabled: true, retentionPolicy: { enabled: true, days: 365 } }
    ]
  }
}

resource appDiagnostics 'Microsoft.Web/sites/providers/diagnosticSettings@2021-05-01-preview' = {
  name: '${appName}/Microsoft.Insights/appservice'
  properties: {
    workspaceId: logAnalytics.id
    logs: [
      { category: 'AppServiceHTTPLogs', enabled: true, retentionPolicy: { enabled: true, days: 90 } }
      { category: 'AppServiceConsoleLogs', enabled: true, retentionPolicy: { enabled: true, days: 90 } }
      { category: 'AppServiceAppLogs', enabled: true, retentionPolicy: { enabled: true, days: 90 } }
      { category: 'AppServiceAuditLogs', enabled: true, retentionPolicy: { enabled: true, days: 365 } }
    ]
    metrics: [
      { category: 'AllMetrics', enabled: true, retentionPolicy: { enabled: true, days: 90 } }
    ]
  }
}

resource kvDiagnostics 'Microsoft.KeyVault/vaults/providers/diagnosticSettings@2021-05-01-preview' = {
  name: '${kvName}/Microsoft.Insights/keyvault'
  properties: {
    workspaceId: logAnalytics.id
    logs: [
      { category: 'AuditEvent', enabled: true, retentionPolicy: { enabled: true, days: 365 } }
    ]
    metrics: [
      { category: 'AllMetrics', enabled: true, retentionPolicy: { enabled: true, days: 90 } }
    ]
  }
}

// ── Static Web App (for frontend) ──
resource swa 'Microsoft.Web/staticSites@2022-09-01' = {
  name: 'swa-oncall-${environmentName}'
  location: location
  properties: {
    repositoryUrl: ''
    branch: 'main'
    buildProperties: {
      appLocation: '/src/frontend'
      apiLocation: ''
      outputLocation: '/dist'
    }
  }
  sku: {
    name: 'Free'
    tier: 'Free'
  }
}

// ── Outputs ──
output appUrl string = 'https://${appName}.azurewebsites.net'
output swaUrl string = swa.properties.defaultHostname
output sqlServerFqdn string = sqlServer.properties.fullyQualifiedDomainName
output keyVaultName string = kvName
output appInsightsConnectionString string = appInsights.properties.InstrumentationKey
