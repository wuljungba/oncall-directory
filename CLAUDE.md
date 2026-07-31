# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Build & Test Commands

### Backend (.NET 8)
```bash
cd src/backend/OnCallApi
dotnet build                           # Build
dotnet build -c Release                # Release build
dotnet run --urls "http://localhost:5000"   # Run API server
dotnet test                            # Run all tests
dotnet watch run                       # Hot reload dev server
```

### Frontend (React + Vite)
```bash
cd src/frontend
npm run dev          # Start dev server on port 5173 (proxies /api → localhost:5000)
npm run build        # TypeScript check + Vite production build
npm run test         # Vitest unit tests
npm run test:e2e     # Playwright end-to-end tests
npm run lint         # ESLint on src/
```

### Infrastructure
```bash
az deployment group create --resource-group rg-oncall-{env} --template-file infrastructure/bicep/main.bicep --parameters environmentName={env} ...
```

## Project Architecture

### System Overview
```
React SPA (port 5173) ──proxy──▶ ASP.NET Core 8 API (port 5000) ──▶ Azure SQL
                                       │
                                  Microsoft Graph API
                                       │
                              SharePoint · Outlook · Teams · AD
```

### Backend Structure (`src/backend/OnCallApi/`)
- **Controllers/** — REST API endpoints. Notable controllers:
  - `ScheduleController.cs`, `DepartmentsController.cs`, `DirectoryController.cs` — core domain
  - `AuthController.cs`, `DevAuthController.cs`, `LocalAuthController.cs` — authentication
  - `TenantsController.cs`, `TenantAdminsController.cs` — multi-tenant admin
  - `PhoneTreesController.cs`, `PhoneTreeEventsController.cs` — emergency phone trees
  - `EscalationController.cs`, `ComplianceController.cs` — escalation engine + HIPAA compliance
  - `IntegrationDiagnosticsController.cs` — M365 connectivity diagnostics
- **Services/** — Business logic layer with background services:
  - `GraphApiService.cs` — all Microsoft Graph calls (lazy-init credential, handles invalid config gracefully)
  - `AdSyncBackgroundService.cs`, `CalendarSyncService.cs`, `DepartmentSyncService.cs`, `PresenceSyncService.cs` — periodic sync services (disabled when interval ≤ 0)
  - `EscalationBackgroundService.cs` — escalation engine
  - `AuditBackgroundService.cs` — HIPAA audit log flush
  - `ScheduleService.cs`, `DirectoryService.cs`, `AdminService.cs` — core domain services
  - `Dispatch/` — Code call dispatch (phone/Twilio integration)
  - `TeamsBotService.cs`, `TeamsNotificationService.cs`, `SharePointPublishingService.cs` — M365 integrations
- **Models/** — Domain models (Employee, Schedule, Shift, Department, Tenant, PhoneTree, etc.)
- **Data/** — EF Core DbContext (`AppDbContext.cs`), factory, migrations
- **Middleware/** — `DevelopmentAuthenticationHandler.cs` (dev auto-auth), `JwtValidationMiddleware.cs`, `HipaaAuditMiddleware.cs`, `TenantClaimsMiddleware.cs`, `ExceptionHandlingMiddleware.cs`
- **Hubs/** — `OnCallNotificationHub.cs` (SignalR real-time)
- **Authentication/** — `LocalJwtService.cs`, `GoogleTokenValidationOptions.cs`
- **Configuration/** — `GraphApiOptions.cs`, `DispatchOptions.cs`

### Frontend Structure (`src/frontend/src/`)
- **pages/** — Route-level components (Dashboard, SchedulePage, DirectoryPage, PhoneTreePage, etc.)
- **components/** — Reusable UI: Layout.tsx, ErrorBoundary.tsx, OnboardingWizard.tsx, Toast.tsx, etc.
- **hooks/** — `useAuth.ts` (multi-provider auth state), `useSignalR.tsx` (real-time connections)
- **services/** — `api.ts` (Axios/fetch client), `auth/` (auth provider abstraction: Microsoft, Google, Local, Factory)
- **utils/** — `validation.ts`

### Infrastructure
- **`infrastructure/bicep/main.bicep`** — Azure resources: SQL Server, Key Vault, App Service + staging slot, Redis Cache, Storage Account, Log Analytics, Application Insights
- **`infrastructure/pipelines/deploy.yml`** — CI/CD pipeline definition
- **`.github/workflows/deploy.yml`** — GitHub Actions: build → test → publish → deploy to staging slot → health check → swap to production

## Key Development Patterns

### Authentication
- **Dev mode** (`VITE_DEV_AUTH=true` in frontend `.env`, `DevAuth:Enabled: true` in backend `appsettings.Development.json`): Auto-authenticates as dev user with all roles. No Entra ID or MSAL needed.
- **Production**: Microsoft Entra ID (primary), Google OAuth, local accounts. Frontend uses MSAL, backend validates via `Microsoft.Identity.Web`.
- Auth provider abstraction: `src/services/auth/authFactory.ts` creates `MicrosoftAuthProvider | GoogleAuthProvider | LocalAuthProvider` based on `sessionStorage`.
- `main.tsx` conditionally initializes MSAL — skipped when `VITE_DEV_AUTH=true` to avoid hanging on placeholder client ID.

### Local Development Setup
1. Backend `.env` secret placeholders are validated at startup — warnings in dev, exceptions in production.
2. Start backend first (`dotnet run` on port 5000), then frontend (`npm run dev` on port 5173).
3. Vite proxies `/api/*` and `/hubs/*` (WebSocket) to `localhost:5000`.
4. SQLite/InMemory database providers are available for local dev without Azure SQL.
5. Background sync services (AD, Calendar, Presence) are **disabled** in dev (`Sync:AdSyncIntervalMinutes: 0`).

### Multi-Tenant Architecture
- `Tenant` entity separates data per organization/business unit.
- `TenantAdmin` with `Admin.Scoped` permission provides scoped admin access.
- `TenantClaimsMiddleware` extracts tenant context from JWT claims.
- Frontend `useAuth` hook manages `activeTenantId` across session.

### HIPAA Compliance
- PHI-sensitive fields use column encryption (EF Core + Always Encrypted).
- `HipaaAuditMiddleware` logs all PHI access requests.
- `AuditBackgroundService` flushes audit logs asynchronously.
- Session timeout, TLS 1.2+, audit retention (default 2190 days / 6 years).

### Real-Time Communication
- SignalR hub at `/hubs/notifications` for live updates.
- Frontend `useSignalR` hook manages connection lifecycle.
- Vite dev proxy handles WebSocket upgrade for SignalR.

### Graph API
- All Microsoft Graph calls go through `GraphApiService` — backend only, never frontend.
- Uses app-only `ClientSecretCredential` (no user delegation).
- Lazy initialization: credential is not created until first Graph call, so placeholder config doesn't crash startup.

## Configuration

### Key Environment Variables (Frontend `.env`)
```
VITE_DEV_AUTH=true              # Dev mode: skip MSAL, use dev user
VITE_AZURE_CLIENT_ID=...        # MSAL SPA client ID
VITE_GOOGLE_CLIENT_ID=...       # Google OAuth client ID
```

### Key App Settings (Backend `appsettings.json`)
```json
{
  "AzureAd": { "Instance", "Domain", "TenantId", "ClientId" },
  "GraphApi": { "TenantId", "ClientId", "ClientSecret" },
  "Authentication": { "Google": { "ClientId" }, "Local": { "SigningKey" } },
  "DevAuth": { "Enabled": true },
  "Sync": { "AdSyncIntervalMinutes", "CalendarSyncIntervalMinutes", "PresenceSyncIntervalMinutes" },
  "ConnectionStrings": { "DefaultConnection" },
  "Cors": { "Origin" },
  "Hipaa": { "SessionTimeoutMinutes", "AuditLogRetentionDays" }
}
```

## Design Documents
See `docs/` directory: `architecture.md`, `oncall-schedule-design.md`, `phone-directory-design.md`, `integration-design.md`, `hipaa-compliance.md`, `deployment-guide.md`.