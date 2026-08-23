# Deployment Checklist

Before deploying to any environment, ensure the following GitHub secrets are configured in the repository settings.

## Required GitHub Secrets

### Frontend Build Secrets

- **`VITE_AZURE_CLIENT_ID`** — Microsoft Entra application client ID for MSAL authentication
  - Format: UUID (e.g., `96955ba3-c70c-4205-8637-a4b34301480a`)
  - Obtain from: Azure Portal → App Registrations → Your app → Application (client) ID

- **`VITE_GOOGLE_CLIENT_ID`** — Google OAuth client ID for Google sign-in
  - Format: Numeric ID with `.apps.googleusercontent.com` domain
  - Obtain from: Google Cloud Console → Credentials → OAuth 2.0 Client IDs

### Azure Deployment Secrets

- **`AZURE_WEBAPP_PUBLISH_PROFILE`** — App Service publish profile for deployment
  - Obtain from: Azure Portal → App Service → Get publish profile (download XML file, copy contents)

## Backend Configuration (appsettings)

The backend uses Key Vault references for sensitive configuration in production. Ensure these are set in Key Vault and referenced in `appsettings.Production.json`:

- **JWT Signing Key** (`Authentication:LocalAuth:SigningKey`)
  - Should be a strong random string (min 32 characters)
  - Default placeholder in appsettings.json must be overridden in production

- **Epic Shared Secret** (`EpicIntegration:SharedSecret`)
  - Generated and managed by Epic Interconnect setup
  - Must be at least as long as specified by Epic

- **Microsoft Graph API Credentials**
  - `AzureAd:ClientSecret` — Application secret for Graph API access
  - `GraphApi:ClientSecret` — App-only permission bearer token secret

- **Twilio Configuration** (if SMS/voice alerts enabled)
  - `Twilio:AccountSid` — Trial accounts (error 572006) cannot send alerts; upgrade to paid account
  - `Twilio:AuthToken` — Auth credentials

## Deployment Verification

After deploying:

1. ✓ Health check passes at `/health` endpoint
2. ✓ Frontend loads without 404s
3. ✓ Auth redirects work (Entra, Google, Local)
4. ✓ SignalR WebSocket connection establishes (`/hubs/notifications`)
5. ✓ Code-call dispatch channels test successfully (if configured)

## Known Limitations

- **Staging → Swap Pipeline** — Currently not implemented. Deployments go directly to production. See infrastructure/pipelines/deploy.yml for the planned staging workflow.
- **Twilio Trial Accounts** — Code-call SMS/voice alerts fail with error 572006. Requires paid account upgrade.
- **Column Encryption (PHI)** — Currently not implemented. Production uses TLS 1.2 transport encryption and implicit Azure SQL TDE. See docs/hipaa-compliance.md.
