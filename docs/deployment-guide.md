# Deployment Guide — OnCall Schedule & Directory

## Overview

This guide covers deploying the OnCall application to Azure for production use. The architecture uses:
- **Azure App Service** (PremiumV2) — serves both the ASP.NET Core API and the React frontend
- **Azure SQL** — General Purpose Serverless (2 vCores, 500 GB)
- **Azure Redis** — Standard tier (for caching at scale)
- **Azure Blob Storage** — GRS (for CSV imports, audit logs, compliance reports)
- **Azure Key Vault** — Secrets management with managed identity access
- **Azure Application Insights** — Monitoring and alerting
- **GitHub Actions** — CI/CD with staging slot swap deployment

---

## Prerequisites

| Item | Details |
|------|---------|
| Azure subscription | With contributor access to create resources |
| Azure CLI | `az --version` should work |
| GitHub Secrets | Configured with Azure credentials |
| Entra ID (Azure AD) | App registration for the API |
| Domain (optional) | Custom domain for `*.azurewebsites.net` fallback works |
| .NET 8 SDK | For local build verification |
| Node.js 20 | For frontend build |

---

## Step 1: Azure Setup

### 1a — Create Entra ID App Registration

```bash
az ad app create \
  --display-name "OnCall API" \
  --sign-in-audience AzureADMyOrg \
  --web-redirect-uris "https://app-oncall-production.azurewebsites.net" "https://app-oncall-production-staging.azurewebsites.net"
```

Note the **Application (client) ID** and **Directory (tenant) ID**.

Create a client secret for the app:
```bash
az ad app credential reset --id <app-id> --years 1
```

Expose an API scope:
- Go to Azure Portal → App Registration → "OnCall API" → Expose an API
- Set Application ID URI to `api://<client-id>`
- Add scope `access_as_user` (Admin and user consent)

### 1b — Configure App Roles

Add these app roles in the Entra ID app registration manifest:
- `OnCall.Viewer` — Read schedules and directory
- `OnCall.Scheduler` — Create/edit schedules
- `OnCall.Admin` — Full admin access

### 1c — Store Secrets in Key Vault

After deploying infrastructure (Step 2), store these secrets in Key Vault:

| Secret Name | Value |
|------------|-------|
| `entra-tenant-id` | Your Azure AD tenant ID |
| `entra-client-id` | Your app registration client ID |
| `entra-domain` | Your domain (e.g., `hospital.onmicrosoft.com`) |
| `sql-admin-password` | Strong password for SQL admin |
| `graph-client-id` | Graph API app registration client ID |
| `graph-client-secret` | Graph API app registration secret |
| `graph-tenant-id` | Tenant ID for Graph API access |

---

## Step 2: Deploy Infrastructure

```bash
# Login to Azure
az login

# Create resource group
az group create --name rg-oncall-production --location eastus

# Deploy Bicep template
az deployment group create \
  --resource-group rg-oncall-production \
  --template-file infrastructure/bicep/main.bicep \
  --parameters infrastructure/bicep/parameters.production.json
```

This creates:
- ✅ Azure SQL Database (General Purpose, 2 vCores, 500 GB, geo-backup)
- ✅ App Service Plan (PremiumV2) + Web App
- ✅ Staging deployment slot
- ✅ Key Vault with SQL connection string secret
- ✅ Azure Redis Cache (Standard C1)
- ✅ Blob Storage (GRS) with containers: `import-files`, `audit-archive`, `compliance-reports`
- ✅ Application Insights + Log Analytics
- ✅ Diagnostic settings for SQL, App Service, Key Vault
- ✅ RBAC role assignments for managed identity

### Configure Managed Identity Access

The web app uses SystemAssigned managed identity. After deployment, grant the identity access to:

```bash
# Grant web app access to SQL via Entra ID
az sql server ad-admin create \
  --resource-group rg-oncall-production \
  --server sql-oncall-production \
  --display-name "OnCall Web App" \
  --object-id $(az webapp identity show --name app-oncall-production --resource-group rg-oncall-production --query principalId -o tsv)
```

---

## Step 3: Configure & Deploy the Application

### 3a — Configure appsettings.json

The `appsettings.json` contains development defaults. For production, override via **App Service app settings** (set in Portal or Bicep):

| Setting | Source | Example |
|---------|--------|---------|
| `ConnectionStrings__DefaultConnection` | Key Vault | `@Microsoft.KeyVault(...)` |
| `AzureAd__Instance` | Fixed | `https://login.microsoftonline.com/` |
| `AzureAd__Domain` | Key Vault | `hospital.onmicrosoft.com` |
| `AzureAd__TenantId` | Key Vault | Entra ID tenant GUID |
| `AzureAd__ClientId` | Key Vault | App registration client ID |
| `Cors__Origin` | Config | `https://app-oncall-production.azurewebsites.net` |
| `ApplicationInsights__ConnectionString` | Bicep output | Auto-configured |
| `DevAuth__Enabled` | Fixed | `false` |
| `Authentication__Local__SigningKey` | Key Vault (generate a 32+ char key) | |
| `GraphApi__TenantId` | Key Vault | For Microsoft Graph access |
| `GraphApi__ClientId` | Key Vault | Graph API app ID |
| `GraphApi__ClientSecret` | Key Vault | Graph API app secret |
| `Sync__AdSyncIntervalMinutes` | Config | `15` |
| `Sync__CalendarSyncIntervalMinutes` | Config | `5` |
| `Sync__PresenceSyncIntervalMinutes` | Config | `2` |
| `Hipaa__SessionTimeoutMinutes` | Config | `15` |
| `Hipaa__AuditLogRetentionDays` | Config | `2190` (6 years) |

### 3b — Build and Deploy

```bash
# Build backend
cd src/backend/OnCallApi
dotnet publish -c Release -o ../../../publish

# Build frontend
cd ../../frontend
npm ci
npm run build

# Copy frontend to publish folder
cp -r dist ../publish/wwwroot

# Deploy via GitHub Actions (recommended)
# Push to main branch triggers automatic deployment
```

### 3c — Using GitHub Actions

The `.github/workflows/deploy.yml` pipeline:

1. Builds .NET backend → `publish/`
2. Installs npm deps + builds frontend → `dist/`
3. Copies `dist/` to `publish/wwwroot`
4. Deploys to **staging slot**
5. Runs health check
6. Swaps staging → production (zero-downtime)

**Required GitHub Secrets:**

| Secret | Description |
|--------|-------------|
| `AZURE_WEBAPP_PUBLISH_PROFILE` | Publish profile for the App Service |
| `AZURE_CREDENTIALS` | Service principal JSON for Azure login |

```bash
# Create publish profile secret (from Azure Portal)
# Go to App Service → Deployment Slots → staging → Get Publish Profile

# Create service principal for Azure login
az ad sp create-for-rbac --name "oncall-github-actions" \
  --role contributor \
  --scopes /subscriptions/<subscription-id>/resourceGroups/rg-oncall-production \
  --sdk-auth
# Copy the JSON output to AZURE_CREDENTIALS secret
```

---

## Step 4: Configure Frontend Auth Providers

### Microsoft Entra ID

The frontend MSAL config reads from environment variables:

| Variable | Source | Example |
|----------|--------|---------|
| `VITE_AZURE_CLIENT_ID` | Entra ID app registration client ID | |
| `VITE_DEV_AUTH` | Set to `false` for production | |

These are set in the App Service configuration (not `.env` files in production).

### Google OAuth (Optional)

| Variable | Source | Example |
|----------|--------|---------|
| `VITE_GOOGLE_CLIENT_ID` | Google Cloud Console → Credentials | |

### Local Accounts

Local accounts use BCrypt + JWT. The JWT signing key must be set via `Authentication:Local:SigningKey` in Key Vault. Generate a strong key:

```bash
openssl rand -base64 32
```

---

## Step 5: Verify Deployment

### 5a — Health Check

```bash
curl https://app-oncall-production.azurewebsites.net/health
```

Expected response:
```json
{"status":"Healthy","checks":[{"name":"database","status":"Healthy"}]}
```

### 5b — Auth Check

```bash
curl https://app-oncall-production.azurewebsites.net/api/auth/me
```

Should return the current user's info and permissions.

### 5c — Data Check

```bash
curl https://app-oncall-production.azurewebsites.net/api/departments
curl https://app-oncall-production.azurewebsites.net/api/code-call-locations
```

Should return seed data (12 departments, 6 code call locations).

### 5d — Frontend Check

Open `https://app-oncall-production.azurewebsites.net` in a browser.
- Landing page should render
- Click "Sign In" → should redirect to Microsoft login
- After login → should show Dashboard

---

## Step 6: Post-Deployment Configuration

### 6a — Create First Admin

If using local accounts:
```bash
curl -X POST https://app-oncall-production.azurewebsites.net/api/auth/local/register \
  -H "Content-Type: application/json" \
  -d '{"email":"admin@hospital.org","password":"<strong-password>","displayName":"Admin","roles":["OnCall.Viewer","OnCall.Scheduler","OnCall.Admin"]}'
```

### 6b — Assign Tenant Admins

```bash
curl -X POST https://app-oncall-production.azurewebsites.net/api/tenants/1/admins \
  -H "Content-Type: application/json" \
  -d '{"azureAdObjectId":"<user-aad-id>","role":"SuperAdmin"}'
```

### 6c — Import Employees

Use the Admin UI (`/admin`) or the CSV import endpoint:
```bash
curl -X POST https://app-oncall-production.azurewebsites.net/api/import/employees \
  -F "file=@employees.csv"
```

### 6d — Configure Integrations

In Admin → Integrations tab:
- Configure AD sync interval
- Enable/disable Teams, Email, SMS notifications
- Configure InformaCast/Vocera/CUCM dispatch endpoints (if using code call features)

---

## Step 7: Monitoring & Alerts

### 7a — Application Insights

Open the Application Insights resource in Azure Portal:
- **Live Metrics** — See real-time request volume and failures
- **Failures** — Review failed requests and exceptions
- **Performance** — Monitor API response times
- **Logs** — Run Kusto queries for custom business metrics

### 7b — Key Metrics to Watch

| Metric | Target | Action if exceeded |
|--------|--------|-------------------|
| CPU (App Service) | <70% | Enable auto-scale |
| Database DTU/vCore | <80% | Scale up or optimize queries |
| Request duration (API) | <500ms avg | Review slow endpoints |
| Health check failures | 0 | Check backend logs |
| Auth failures | <1% of requests | Check Entra ID configuration |

### 7c — Dashboard

An Azure Dashboard is defined in `infrastructure/bicep/dashboard.bicep`. Deploy it:

```bash
az deployment group create \
  --resource-group rg-oncall-production \
  --template-file infrastructure/bicep/dashboard.bicep
```

---

## Scaling Guide (5000+ Users)

| Resource | Current | At 5000 Users | Cost Impact |
|----------|---------|---------------|-------------|
| App Service | P1v2 (1 core) | P1v2 auto-scale (1-3 instances) | ~$175-350/mo |
| SQL Database | GP_S_Gen5 (2 vCores) | GP_S_Gen5 (4 vCores) | ~$700-900/mo |
| Redis Cache | Standard C0 (250 MB) | Standard C1 (1 GB) | ~$55/mo |
| Blob Storage | Standard_GRS | Same (cost is per GB) | ~$5/mo |
| Front Door + WAF | Not configured | Standard_AzureFrontDoor | ~$35/mo |

**Enable auto-scaling:**
```bash
az deployment group create \
  --resource-group rg-oncall-production \
  --template-file infrastructure/bicep/autoscale.bicep
```

**Enable Front Door + WAF:**
```bash
az deployment group create \
  --resource-group rg-oncall-production \
  --template-file infrastructure/bicep/frontdoor.bicep
```

---

## Troubleshooting

| Problem | Check |
|---------|-------|
| App returns 500 | Check App Service logs → "Log stream" |
| Health check fails | Check `ConnectionStrings__DefaultConnection` in app settings |
| Auth fails on login | Check `AzureAd__*` settings match Entra ID app registration |
| Frontend shows blank page | Check browser console for errors; verify `dist/` was deployed |
| API returns 401 | Check `DevAuth__Enabled` is `false`; check JWT bearer token |
| Database connection fails | Verify firewall rule allows Azure services; check Key Vault secret |
| CSV import fails | Check file format; ensure `import-files` container exists in blob storage |

---

## Rollback

```bash
# Swap staging and production back
az webapp deployment slot swap \
  --name app-oncall-production \
  --resource-group rg-oncall-production \
  --slot staging \
  --target-slot production

# Or redeploy previous version from GitHub Actions
# Go to Actions → Select previous successful run → Re-run
```

---

## Security Checklist

- [ ] Entra ID app registration configured with correct redirect URIs
- [ ] App roles assigned to users/groups
- [ ] Key Vault secrets populated (not placeholder values)
- [ ] `DevAuth__Enabled` set to `false`
- [ ] SQL firewall restricts access to Azure services only
- [ ] HTTPS-only enabled (set in Bicep)
- [ ] TLS 1.2 minimum enforced
- [ ] CORS origin set to specific domain (not `*`)
- [ ] Audit diagnostic settings enabled
- [ ] HIPAA compliance documentation updated
- [ ] Managed identity used (not connection strings in config)
- [ ] Blob storage public access disabled
- [ ] Redis non-SSL port disabled
- [ ] Regular security reviews scheduled