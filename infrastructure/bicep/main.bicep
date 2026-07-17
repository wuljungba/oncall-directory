param location string = 'eastus'
param environmentName string = 'dev'
param sqlAdminPassword string

var resourceGroupName = 'rg-oncall-${environmentName}'
var appName = 'app-oncall-${environmentName}'
var sqlServerName = 'sql-oncall-${environmentName}'
var sqlDbName = 'sqldb-oncall-${environmentName}'

// ── Resource Group ──
resource rg 'Microsoft.Resources/resourceGroups@2023-07-01' = {
  name: resourceGroupName
  location: location
}

// ── Azure SQL Database ──
resource sqlServer 'Microsoft.Sql/servers@2023-08-01-preview' = {
  name: sqlServerName
  location: location
  properties: {
    administratorLogin: 'oncalladmin'
    administratorLoginPassword: sqlAdminPassword
    minimalTlsVersion: '1.2'
    publicNetworkAccess: 'Enabled'
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
  properties: {
    serverFarmId: appPlan.id
    httpsOnly: true
    siteConfig: {
      netFrameworkVersion: 'v8.0'
      alwaysOn: true
      minTlsVersion: '1.2'
      appSettings: [
        { name: 'ConnectionStrings__DefaultConnection', value: 'Server=tcp:${sqlServer.properties.fullyQualifiedDomainName},1433;Database=${sqlDbName};User ID=oncalladmin;Password=${sqlAdminPassword};TrustServerCertificate=False;Encrypt=True;' }
        { name: 'AzureAd__Instance', value: 'https://login.microsoftonline.com/' }
        { name: 'AzureAd__Domain', value: 'your-tenant.onmicrosoft.com' }
        { name: 'AzureAd__TenantId', value: 'your-tenant-id' }
        { name: 'AzureAd__ClientId', value: 'your-api-client-id' }
        { name: 'Cors__Origin', value: 'https://${appName}.azurewebsites.net' }
        { name: 'WEBSITE_RUN_FROM_PACKAGE', value: '1' }
      ]
    }
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
