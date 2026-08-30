param location string = 'eastus2'
param environmentName string = 'dev'
@secure()
param sqlAdminPassword string
param sqlAdminLogin string = 'oncalladmin'
param entraTenantId string
param entraClientId string
param entraDomain string
param corsOrigin string = ''

// ── Twilio SMS dispatch (code-call alerts to the on-call provider) ──
// The Auth Token is NOT a parameter: it is read at runtime from the Key Vault secret
// 'TwilioAuthToken', so the credential never passes through a deployment parameter file
// or the deployment history. Leave twilioEnabled=false until the secret exists.
param twilioEnabled bool = false
param twilioAccountSid string = ''
param twilioFromNumber string = ''
param twilioMessagingServiceSid string = ''

// ── Microsoft Graph (AD sync, calendar push, presence, Teams) ──
// Client secret comes from the Key Vault secret 'GraphApiClientSecret'; only the
// non-sensitive identifiers are parameters.
param graphTenantId string = ''
param graphClientId string = ''

// ── Identity ──
// The Google client id is a public identifier (it ships inside the frontend bundle), so
// it is a plain parameter. The local JWT signing key is a secret and is read from the
// Key Vault secret 'LocalJwtSigningKey'.
param googleClientId string = ''

// Super administrators, by email and/or Entra object id. Without at least one entry
// nobody can hold Admin.Full, so an environment deployed with empty lists has no
// administrator at all.
param superAdminEmails array = []
param superAdminObjectIds array = []

// ── Background sync intervals (minutes; 0 disables the service) ──
param adSyncIntervalMinutes int = 15
param calendarSyncIntervalMinutes int = 5
param presenceSyncIntervalMinutes int = 2

// ── Scheduling ──
// Which wall clock generated rotations follow. IANA or Windows id.
param schedulingTimeZone string = 'America/New_York'

// ── HIPAA ──
param hipaaSessionTimeoutMinutes int = 15
param hipaaAuditLogRetentionDays int = 2190

// The API's audience, which token validation checks. Defaults to the app ID URI derived
// from the SPA client id. Omitting this from the template would delete it on redeploy and
// break every sign-in.
param entraAudience string = ''

// Days of HTTP logs the platform retains.
param httpLoggingRetentionDays int = 3

var resourceGroupName = 'rg-oncall-${environmentName}'
var appName = 'app-oncall-${environmentName}'
var sqlServerName = 'sql-oncall-${environmentName}'
var sqlDbName = 'sqldb-oncall-${environmentName}'
var kvName = 'kv-${take(environmentName, 4)}-${uniqueString(subscription().subscriptionId)}'
var aiName = 'ai-oncall-${environmentName}'
var logName = 'log-oncall-${environmentName}'
var stName = 'st${take(environmentName, 4)}${uniqueString(subscription().subscriptionId)}'
var connectionString = 'Server=tcp:${sqlServer.properties.fullyQualifiedDomainName},1433;Database=${sqlDbName};User ID=${sqlAdminLogin};Password=${sqlAdminPassword};TrustServerCertificate=False;Encrypt=True;'
var defaultCorsOrigin = !empty(corsOrigin) ? corsOrigin : 'https://${appName}.azurewebsites.net'

// Twilio posts delivery status here. Slot-specific, so a message sent from staging
// settles on staging rather than reporting into production's dispatch history.
var twilioStatusCallbackProd = 'https://${appName}.azurewebsites.net/api/public/twilio/status'
var twilioStatusCallbackStaging = 'https://${appName}-staging.azurewebsites.net/api/public/twilio/status'
var twilioSettings = [
  { name: 'Dispatch__Twilio__Enabled', value: string(twilioEnabled) }
  { name: 'Dispatch__Twilio__AccountSid', value: twilioAccountSid }
  { name: 'Dispatch__Twilio__AuthToken', value: '@Microsoft.KeyVault(SecretUri=https://${kvName}.vault.azure.net/secrets/TwilioAuthToken)' }
  { name: 'Dispatch__Twilio__FromNumber', value: twilioFromNumber }
  { name: 'Dispatch__Twilio__MessagingServiceSid', value: twilioMessagingServiceSid }
]

// ── Every remaining app setting the application reads ──
//
// These were previously set by hand on the running slots and existed nowhere in this
// template. Because ARM replaces the appSettings collection wholesale, re-running this
// file silently DELETED all of them — the Graph credentials, the JWT signing key and the
// super-admin list — leaving an app that could not sync, could not validate a local token,
// and had no administrator. Declaring them here is what makes the template safe to re-run.
//
// Secrets are Key Vault references, so no credential appears in the template, a parameter
// file, or the deployment history. Create these secrets before deploying:
//   GraphApiClientSecret, LocalJwtSigningKey, SqlConnectionString, TwilioAuthToken
var kvRef = 'https://${kvName}.vault.azure.net/secrets'

var superAdminEmailSettings = [for (email, i) in superAdminEmails: {
  name: 'Authentication__SuperAdmins__Emails__${i}'
  value: email
}]

var superAdminObjectIdSettings = [for (objectId, i) in superAdminObjectIds: {
  name: 'Authentication__SuperAdmins__ObjectIds__${i}'
  value: objectId
}]

var applicationSettings = concat([
  { name: 'GraphApi__TenantId', value: empty(graphTenantId) ? entraTenantId : graphTenantId }
  { name: 'GraphApi__ClientId', value: graphClientId }
  { name: 'GraphApi__ClientSecret', value: '@Microsoft.KeyVault(SecretUri=${kvRef}/GraphApiClientSecret)' }
  { name: 'Authentication__Google__ClientId', value: googleClientId }
  { name: 'Authentication__Local__SigningKey', value: '@Microsoft.KeyVault(SecretUri=${kvRef}/LocalJwtSigningKey)' }
  { name: 'Sync__AdSyncIntervalMinutes', value: string(adSyncIntervalMinutes) }
  { name: 'Sync__CalendarSyncIntervalMinutes', value: string(calendarSyncIntervalMinutes) }
  { name: 'Sync__PresenceSyncIntervalMinutes', value: string(presenceSyncIntervalMinutes) }
  { name: 'Scheduling__TimeZone', value: schedulingTimeZone }
  { name: 'Hipaa__SessionTimeoutMinutes', value: string(hipaaSessionTimeoutMinutes) }
  { name: 'Hipaa__AuditLogRetentionDays', value: string(hipaaAuditLogRetentionDays) }
  { name: 'AzureAd__Audience', value: empty(entraAudience) ? 'api://${entraClientId}' : entraAudience }
  { name: 'WEBSITE_HTTPLOGGING_RETENTION_DAYS', value: string(httpLoggingRetentionDays) }
  // Db:Recreate drops the database. The application refuses it outside Development, and
  // it is pinned false here so the setting can never be left over from a manual change.
  { name: 'Db__Recreate', value: 'false' }
], concat(superAdminEmailSettings, superAdminObjectIdSettings))

// ── Application Insights + Log Analytics ──
resource logAnalytics 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: logName
  location: location
  properties: {
    sku: { name: 'PerGB2018' }
    // Diagnostics only, and priced by ingestion. The HIPAA audit trail is kept
    // in SQL for 2190 days and is unaffected by this.
    retentionInDays: 30
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

// ── Azure SQL Database (Standard, DTU) ──
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
    name: 'S0'
    tier: 'Standard'
  }
  properties: {
    collation: 'SQL_Latin1_General_CP1_CI_AS'
    maxSizeBytes: 268435456000 // 250 GB - Standard tier maximum
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

// ── App Service Plan + Web App (serves both API and frontend static files) ──
resource appPlan 'Microsoft.Web/serverfarms@2023-12-01' = {
  name: 'plan-oncall-${environmentName}'
  location: location
  sku: {
    name: 'P0v3'
    tier: 'PremiumV3'
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
      appSettings: concat([
        { name: 'ConnectionStrings__DefaultConnection', value: '@Microsoft.KeyVault(SecretUri=https://${kvName}.vault.azure.net/secrets/SqlConnectionString)' }
        { name: 'AzureAd__Instance', value: replace(environment().authentication.loginEndpoint, '/$', '') }
        { name: 'AzureAd__Domain', value: entraDomain }
        { name: 'AzureAd__TenantId', value: entraTenantId }
        { name: 'AzureAd__ClientId', value: entraClientId }
        { name: 'Cors__Origin', value: defaultCorsOrigin }
        { name: 'ApplicationInsights__ConnectionString', value: appInsights.properties.ConnectionString }
        { name: 'Storage__ConnectionString', value: storageAccount.properties.primaryEndpoints.blob }
        { name: 'WEBSITE_RUN_FROM_PACKAGE', value: '1' }
        { name: 'DevAuth__Enabled', value: 'false' }
      ], concat(applicationSettings, concat(twilioSettings, [
        { name: 'Dispatch__Twilio__StatusCallbackUrl', value: twilioStatusCallbackProd }
      ])))
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
      appSettings: concat([
        { name: 'ConnectionStrings__DefaultConnection', value: '@Microsoft.KeyVault(SecretUri=https://${kvName}.vault.azure.net/secrets/SqlConnectionString)' }
        { name: 'AzureAd__Instance', value: replace(environment().authentication.loginEndpoint, '/$', '') }
        { name: 'AzureAd__Domain', value: entraDomain }
        { name: 'AzureAd__TenantId', value: entraTenantId }
        { name: 'AzureAd__ClientId', value: entraClientId }
        { name: 'Cors__Origin', value: defaultCorsOrigin }
        { name: 'ApplicationInsights__ConnectionString', value: appInsights.properties.ConnectionString }
        { name: 'Storage__ConnectionString', value: storageAccount.properties.primaryEndpoints.blob }
        { name: 'WEBSITE_RUN_FROM_PACKAGE', value: '1' }
        { name: 'DevAuth__Enabled', value: 'false' }
      ], concat(applicationSettings, concat(twilioSettings, [
        { name: 'Dispatch__Twilio__StatusCallbackUrl', value: twilioStatusCallbackStaging }
      ])))
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
output appInsightsConnectionString string = appInsights.properties.ConnectionString