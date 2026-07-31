# Baseline: Authentication & Graph Integration Discovery Report

**Date**: 2026-07-30
**Scope**: Identity & Graph Integration Specialist
**Files examined**: All backend auth controllers, middleware, Graph service, auth config, plus frontend auth providers, useAuth hook, and main.tsx.

---

## 1. Request JWT Validation Pipeline (Production)

When a request arrives at the backend, it passes through this chain:

```
HTTP Request
  |
  v
ExceptionHandlingMiddleware
  |
  v
ASP.NET UseAuthentication() / UseAuthorization()
  |  -- invokes JWT bearer handler(s)
  v
JwtValidationMiddleware (skipped in dev mode)
  |
  v
TenantClaimsMiddleware
  |
  v
HipaaAuditMiddleware
  |
  v
Controller / Endpoint
```

### 1a. Multi-Provider JWT Routing (Program.cs, lines 58-189)

The backend does NOT use a single JWT handler. Instead it registers three schemes and routes by `iss` claim:

| Issuer | Scheme | How it validates |
|--------|--------|------------------|
| `login.microsoftonline.com/{tenant}/v2.0` | `Bearer` (default) | via `Microsoft.Identity.Web` + custom `IssuerValidator` |
| `https://accounts.google.com` | `"Google"` | Standard `AddJwtBearer` with Google's OIDC authority; JWKS resolved automatically |
| `oncall-directory` | `"Local"` | Symmetric HMAC-SHA256 via `LocalJwtService.GetValidationParameters()` |

The **ForwardDefaultSelector** (lines 159-188) reads each JWT's `iss` claim at runtime:

```csharp
return jwt.Issuer switch
{
    "https://accounts.google.com" or "accounts.google.com" => "Google",
    LocalJwtService.Issuer => "Local",
    _ => JwtBearerDefaults.AuthenticationScheme // Microsoft
};
```

If the token can't be parsed as a JWT, it falls back to the Microsoft handler.

### 1b. Microsoft Entra ID Validation (Microsoft.Identity.Web)

The default `"Bearer"` scheme is configured via `AddMicrosoftIdentityWebApi(builder.Configuration.GetSection("AzureAd"))`. This pulls from `appsettings.json`:

```json
"AzureAd": {
  "Instance": "https://login.microsoftonline.com/",
  "Domain": "organizations",
  "TenantId": "organizations",
  "ClientId": "your-api-client-id",
  "Scopes": "api://your-api-client-id/access_as_user"
}
```

A **PostConfigure** step (lines 139-189) overrides `ValidIssuer` with a custom `IssuerValidator` that:
- Accepts any Azure AD tenant issuer (`login.microsoftonline.com/{tid}/v2.0`)
- Rejects `common` and `consumers` issuers
- Validates the tenant ID segment is a valid GUID

This makes the API **multi-tenant**: any Azure AD tenant can authenticate users, as long as they have the right app registration.

At token validation time, `Microsoft.Identity.Web` checks:
- Signature (against the Azure AD JWKS endpoint)
- Audience (must match `AzureAd:ClientId` or its `api://` URI)
- Issuer (custom validator, see above)
- Lifetime (not expired, not before validity)

### 1c. Required Claims Checked by JwtValidationMiddleware

After ASP.NET auth completes, `JwtValidationMiddleware` (in `Middleware/JwtValidationMiddleware.cs`) runs and enforces:

1. **User is authenticated** -- rejects anonymous requests with 401
2. **`access_as_user` scope** (`scp` claim) -- rejects with 403 if missing. This scope is injected by each provider's `OnTokenValidated` event (Google and Local), or provided by Microsoft.Identity.Web for Microsoft tokens.
3. **User identifier claim** -- one of `oid`, `sub`, or `NameIdentifier` must exist (required for audit logging)
4. **Tenant ID** -- for Microsoft/provider tokens (`auth_provider` is null or "microsoft"), the `tid` claim must be a valid non-"common" tenant ID. Rejects with 401 if invalid.

**Protected endpoints** (lines 28-37): The middleware only enforces these checks on paths starting with: `/api/directory`, `/api/schedule`, `/api/phone-trees`, `/api/compliance`, `/api/settings`, `/api/integrations`, `/api/admin`. Other endpoints (e.g., `/api/auth/local/login`, `/health`) are not scoped-checked but still require authentication if they have the `[Authorize]` attribute.

### 1d. Authorization Policies (Program.cs, lines 192-250)

The backend uses two parallel authorization systems:

**Role-based policies** (from `ClaimTypes.Role`):
- `RequireAdmin` → role `OnCall.Admin`
- `RequireScheduler` → role `OnCall.Scheduler` or `OnCall.Admin`
- `RequireViewer` → role `OnCall.Viewer`, `OnCall.Scheduler`, or `OnCall.Admin`

**Permission-based policies** (from `ClaimTypes.Permission`):
- `RequireScheduleRead`, `RequireScheduleWrite`, `RequireDirectoryRead`, `RequireDirectoryWrite`
- `RequireAdminFull`, `RequireAdminScoped`, `RequireTenantManage`
- `RequireCodeCallWrite`
- `RequireAdminFullOrScoped` (assertion: has Admin.Full or Admin.Scoped)
- `RequireAdminFullOrTenantManage` (assertion: has Admin.Full or Tenant.Manage)

Roles and permissions are mapped in `Authorization/Permissions.cs`:
- `OnCall.Viewer` → `Schedule.Read` + `Directory.Read`
- `OnCall.Scheduler` → adds `Schedule.Write` + `CodeCall.Write`
- `OnCall.Admin` → adds `Directory.Write` + `Admin.Full` + `Tenant.Manage`

### 1e. Where Tenant Context Comes From

**Microsoft tokens**: The `tid` claim from the JWT identifies the Azure AD tenant, but this is NOT the application's `Tenant` entity. The app-level tenant comes from `TenantClaimsMiddleware`.

**TenantClaimsMiddleware** (lines 22-148):
1. Looks up `TenantAdmin` records in the database matching the user's `AzureAdObjectId` (from the `oid` claim)
2. For each matching record, adds:
   - `TenantId:{id}` claim with the admin's role (`DepartmentAdmin` or `SuperAdmin`)
   - Scoped admin permissions (`Schedule.Read`, `Schedule.Write`, `Directory.Read`, `Directory.Write`, `CodeCall.Write`, `Admin.Scoped`)
3. **Lazy auto-assignment**: If the user has Azure AD group membership claims (`groups` or `groups:id`), checks if any group matches a `Tenant.AzureAdGroupId`. If so, auto-creates a `TenantAdmin` record with role `DepartmentAdmin` on the fly.

**Graceful degradation** (lines 57-64): If the DB query fails (tables don't exist, migration not applied), the middleware logs a warning and continues. The app works but without tenant scoping.

### 1f. AuthController `/api/auth/me`

Returns the current user's identity extracted from claims:
- `Id`, `Name`, `Email` (from standard claims)
- `Roles` (from `ClaimTypes.Role`)
- `Permissions` (from `Permission` claim)
- `TenantIds` / `TenantRoles` (from `TenantId:{id}` claims)

---

## 2. Dev Mode Bypass

### 2a. How Dev Mode is Activated

Two independent switches, both must be set:

| Layer | Switch | Default |
|-------|--------|---------|
| Backend | `DevAuth:Enabled: true` in `appsettings.Development.json` | not committed to main |
| Frontend | `VITE_DEV_AUTH=true` in `.env` | not committed to main |

### 2b. Backend Behavior When DevAuth is Enabled

Instead of the multi-provider JWT pipeline, the backend registers a single custom authentication handler:

```csharp
builder.Services.AddAuthentication(DevelopmentAuthenticationHandler.SchemeName)
    .AddScheme<AuthenticationSchemeOptions, DevelopmentAuthenticationHandler>(
        DevelopmentAuthenticationHandler.SchemeName, null);
```

**DevelopmentAuthenticationHandler** (`Middleware/DevelopmentAuthenticationHandler.cs`):
- Auto-authenticates **every request** without checking any token
- Reads the `X-Dev-Role` cookie (set by `POST /api/auth/dev/set-role`)
- Default role: `admin` (all three roles: `OnCall.Viewer`, `OnCall.Scheduler`, `OnCall.Admin`)
- Role-switching via `X-Dev-Role` cookie values: `viewer`, `scheduler`, `admin`
- Provides fake claims that satisfy `JwtValidationMiddleware` and `HipaaAuditMiddleware`:
  - `scp: access_as_user`
  - `oid: 00000000-0000-0000-0000-000000000001`
  - `tid: 00000000-0000-0000-0000-000000000002`
  - Permission claims mapped from roles
  - `TenantId:1` claim (value = `SuperAdmin` for admin role, `DepartmentAdmin` otherwise)

**JwtValidationMiddleware is SKIPPED** in dev mode (line 371-376 of Program.cs):
```csharp
if (!devAuthEnabled)
{
    app.UseMiddleware<JwtValidationMiddleware>();
}
```

This means **scope checking and tenant ID validation do not run** in dev mode.

### 2c. Frontend Behavior When VITE_DEV_AUTH=true

In `main.tsx`:
- MSAL initialization is skipped entirely (no `PublicClientApplication`, no `MsalProvider`)
- The app renders without the MSAL context wrapper

In `useAuth.ts`:
- `isLoading` starts as `false` (not `true`)
- `user` is pre-set to a dummy `{ id: 'dev', name: 'dev@local', email: 'dev@local', provider: 'microsoft' }`
- `permissions` pre-set to all permissions (`Schedule.Read`, `Schedule.Write`, `Directory.Read`, `Directory.Write`, `Admin.Full`)
- `isAuthenticated` always returns `true`
- `signIn()` immediately sets the user without any provider interaction, then calls `/api/auth/me` to get fresh permissions

### 2d. Comparison: Dev vs. Production

| Aspect | Development | Production |
|--------|-------------|------------|
| Token required | No | Yes (JWT) |
| MSAL initialized | No | Yes |
| Auth provider | Hardcoded fake user | Microsoft / Google / Local |
| JWT scope check | Skipped | Enforced |
| Tenant ID validation | Skipped | Enforced |
| Multi-tenant routing | Skipped | ForwardDefaultSelector |
| Tenant claims | Hardcoded as tenant ID 1 | From DB TenantAdmin records |
| Authorization policies | Enforced (same policies) | Enforced |
| Cookie-based role switching | Yes (`X-Dev-Role`) | No |

---

## 3. GraphApiService Authentication to Microsoft Graph

### 3a. Authentication Method

`GraphApiService` (`Services/GraphApiService.cs`) uses **app-only** authentication:

```csharp
var creds = new ClientSecretCredential(
    _options.Value.TenantId,
    _options.Value.ClientId,
    _options.Value.ClientSecret);
_client = new GraphServiceClient(creds);
```

There is **no `.WithScopes()` call** on the `ClientSecretCredential`. The `GraphServiceClient` for `ClientSecretCredential` automatically uses the default scope `https://graph.microsoft.com/.default`, which requests **all API permissions** granted to the app registration.

### 3b. Lazy Initialization

The credential is created lazily on the first call to `GetClient()` (line 25-49). This means:
- If `GraphApi:ClientId` / `ClientSecret` are placeholders in dev, the app still starts up
- The first actual Graph call will fail with an error, logged and handled gracefully
- A flag `_clientInitialized` tracks that initialization was attempted, so repeated calls don't retry

### 3c. Actual Graph API Operations (Scopes/Permissions in Use)

Since the credential uses `.default`, the effective scopes are whatever is **pre-configured** on the Entra ID app registration's "API permissions" blade. The code performs these operations:

| Operation | Graph API endpoint | Required Entra permission |
|-----------|-------------------|---------------------------|
| List users | `GET /users` | `User.Read.All` (app) |
| List users delta | `GET /users/delta` | `User.Read.All` (app) |
| Get user presence | `GET /users/{id}/presence` | `Presence.Read.All` (app) |
| Send Teams message | `POST /users/{id}/chats/{id}/messages` | `Chat.ReadWrite.All` (app) |
| List user chats | `GET /users/{id}/chats` | `Chat.Read.All` (app) |
| Create calendar event | `POST /users/{id}/calendar/events` | `Calendars.ReadWrite` (app) |
| List groups | `GET /groups` | `Group.Read.All` (app) |
| Get group members | `GET /groups/{id}/members` | `GroupMember.Read.All` (app) |
| Create SharePoint page | `POST /sites/{id}/lists/SitePages/items` | `Sites.ReadWrite.All` (app) |

### 3d. Configured vs. Actual Permissions

The app registration for `GraphApi` **must** have these application permissions granted and admin-consented:
- `User.Read.All`
- `Presence.Read.All`
- `Chat.ReadWrite.All` (or at least `Chat.Read.All`)
- `Calendars.ReadWrite`
- `Group.Read.All`
- `GroupMember.Read.All`
- `Sites.ReadWrite.All`

The configuration (`GraphApiOptions`) only holds `TenantId`, `ClientId`, and `ClientSecret` -- no scope list is configured in `appsettings.json`. This means there is **no runtime validation** that the required permissions are granted. A 403 from Graph will surface only at runtime as a logged error in each method's catch block.

### 3e. Note: Microsoft.Identity.Web vs. GraphApiService

These are two separate app registrations:
- **AzureAd** section = the SPA's API registration (used by MSAL for user sign-in)
- **GraphApi** section = the server-side app registration (used by `ClientSecretCredential` for app-only calls)

They should be **different** Entra ID app registrations with different client IDs and different permissions.

---

## 4. Google and Local Auth Placement

### 4a. Google Auth

**Frontend** (`googleAuthProvider.ts`):
- Uses Google Identity Services (GIS) credential flow
- `VITE_GOOGLE_CLIENT_ID` configures the Google OAuth client ID
- The credential (a JWT ID token) is stored in `sessionStorage` as the `accessToken`
- The credential is sent as `Authorization: Bearer <credential>` to the backend
- **No token refresh mechanism** -- `getAccessToken()` returns the stored value indefinitely

**Backend** (Program.cs, lines 74-108):
- Registered as a named JWT bearer scheme `"Google"`
- Authority: `https://accounts.google.com`
- Audience validated against `Authentication:Google:ClientId`
- Signing keys resolved from Google's JWKS endpoint automatically by the framework
- `OnTokenValidated` event adds:
  - `auth_provider: google`
  - `scp: access_as_user`
  - `oid: google-{sub}` (prefixed to avoid collision with Microsoft oids)
  - Role: `OnCall.Viewer` (all Google users get default Viewer access)
- **Google users always get Viewer role** -- there is no mechanism to promote them to Scheduler or Admin via Google alone

### 4b. Local Auth

**Frontend** (`localAuthProvider.ts`):
- `signIn(email, password)` calls `POST /api/auth/local/login`
- Backend returns a JWT, stored in `sessionStorage`
- No refresh mechanism

**Backend** (`LocalAuthController.cs`):
- `POST /register` -- admin-only, creates a local account with roles
- `POST /login` -- validates credentials, returns JWT via `LocalJwtService`
- `POST /change-password` -- authenticated user changes own password
- `POST /{id}/reset-password` -- admin-only

**LocalJwtService** (`Authentication/LocalJwtService.cs`):
- HMAC-SHA256 symmetric key signing
- Issuer: `oncall-directory`
- Audience: `oncall-api`
- Default expiry: 1440 minutes (24 hours)
- **Development fallback**: If `Authentication:Local:SigningKey` is missing or < 32 chars, falls back to a hardcoded string `"dev-local-jwt-signing-key-at-least-32-chars!!"` (line 118)
- Claims generated: `NameIdentifier` (format `local-{id}`), `Email`, `Name`, `auth_provider: local`, `scp: access_as_user`, `oid: local-{id}`, roles, optional `employee_id`

**Backend** (`Program.cs`, lines 112-134):
- Registered as named scheme `"Local"`
- `TokenValidationParameters` are injected via `PostConfigure<LocalJwtService>` -- symmetric key, issuer, audience validation
- `OnTokenValidated` adds `auth_provider: local` and `scp: access_as_user`

### 4c. How They Fit Alongside Entra

All three providers produce a `ClaimsPrincipal` with:
- `scp: access_as_user` -- satisfies `JwtValidationMiddleware`
- A user identifier claim (`oid`) -- satisfies audit requirement
- Role claims (`OnCall.Viewer`, etc.) -- satisfy authorization policies

The **ForwardDefaultSelector** routes by `iss`, so they coexist without conflict.

### 4d. Frontend Provider Selection

In `authFactory.ts`:
- `getAuthProvider(type?)` -- if no type given, reads `sessionStorage.getItem('authProvider')`, defaults to `'microsoft'`
- Providers are cached in a module-level `Map<string, IAuthProvider>`
- `getAllProviders()` exists but is **not called anywhere** in the codebase -- there is no visible "switch provider" UI
- `clearProviders()` is called on sign-out in `useAuth.ts`
- `getActiveProviderType()` is used in `useAuth` to set `authProvider` state

The provider is selected implicitly by which sign-in UI the user interacts with (Microsoft popup, Google One Tap, or email/password form). The `sessionStorage` persists the choice across page refreshes.

---

## 5. Notable Gaps and Observations

### G1. Graph API scopes are implicit, not explicit
`GraphApiService` never calls `.WithScopes()` on the credential. It relies entirely on `https://graph.microsoft.com/.default`, which grants all app permissions pre-configured on the Entra registration. If the registration's permissions change, there is no validation at startup or runtime -- failures happen silently inside each method's try/catch.

### G2. No Graph API health check
There is no startup health check that validates the Graph credential works. The `IntegrationDiagnosticsController` provides dispatch channel tests but not a `GET /users/me` Graph connectivity test.

### G3. Google auth tokens do not refresh
`GoogleAuthProvider.getAccessToken()` returns the same stored credential until sign-out. Google ID tokens expire after 1 hour. After expiry, API calls will fail with 401. The `useAuth` hook does not handle this scenario.

### G4. Local JWT development fallback is a security warning
The hardcoded `dev-local-jwt-signing-key-at-least-32-chars!!` in `LocalJwtService.cs` is flagged by `ValidateSecret` at startup (line 41 of Program.cs) but only as a warning in development. In production, it throws. This is correct behavior but worth noting.

### G5. Dev mode vs. production: different behavior surface
- `JwtValidationMiddleware` runs in production but is **entirely skipped** in dev mode
- Tenant claims are hardcoded in dev (`TenantId:1`) vs. loaded from DB in production
- Dev mode uses cookie-based role switching that doesn't exist in production

### G6. `access_as_user` scope is added post-validation
For Google and Local tokens, the `access_as_user` scope is added in `OnTokenValidated` -- *after* the JWT's signature and audience are validated but *before* `JwtValidationMiddleware` runs. This means the middleware's scope check is effectively checking a claim that the middleware pipeline itself injected. If either `OnTokenValidated` event were removed, Google and Local tokens would immediately fail the scope check.

### G7. `getAllProviders()` in authFactory is dead code
The function exists but has no callers. The UI only ever uses `getAuthProvider()` for the currently active provider.

### G8. MSAL client IDs: frontend and backend must match
The frontend's `VITE_AZURE_CLIENT_ID` becomes the MSAL `clientId` and the `api://{clientId}/access_as_user` token request scope. The backend's `AzureAd:ClientId` must be the **same** client ID for audience validation to succeed. If they diverge, Microsoft tokens will be rejected.

### G9. TenantClaimsMiddleware silently swallows DB errors
If the `Tenants`/`TenantAdmins` tables don't exist, or any DB error occurs, the middleware logs a warning and continues. This is deliberate for zero-downtime deployments where migrations haven't run yet, but it means multi-tenant scoping is invisible until the migration completes. Functions that depend on tenant context will work but return empty tenant claims.

### G10. `auth_provider` claim is used inconsistently
`JwtValidationMiddleware` checks `auth_provider` to decide whether to validate tenant ID (lines 96-109). But `auth_provider` is only set by Google and Local `OnTokenValidated` events -- it's not explicitly set by Microsoft.Identity.Web. When it's null (Microsoft tokens), the middleware treats it as "microsoft" and validates tenant ID. If a future provider doesn't set `auth_provider`, it defaults to Microsoft tenant validation, which might be incorrect.

---

## 6. Configuration Entropy Summary

| Config key | Where used | Default (placeholder) | Production must override? |
|-----------|-----------|----------------------|-------------------------|
| `AzureAd:ClientId` | Program.cs (Microsoft.Identity.Web) | `"your-api-client-id"` | Yes |
| `GraphApi:ClientId` | GraphApiService | `"your-graph-client-id"` | Yes |
| `GraphApi:ClientSecret` | GraphApiService | `"your-graph-client-secret"` | Yes |
| `GraphApi:TenantId` | GraphApiService | `"your-home-tenant-id"` | Yes |
| `Authentication:Google:ClientId` | Program.cs (Google JWT) | `"your-google-client-id.apps.googleusercontent.com"` | Yes |
| `Authentication:Local:SigningKey` | LocalJwtService | `"change-me-to-a-32-char-min-secret-key!!"` | Yes |
| `VITE_AZURE_CLIENT_ID` | microsoftAuthProvider.ts | `"your-api-client-id"` | Yes |
| `VITE_GOOGLE_CLIENT_ID` | googleAuthProvider.ts | `""` (empty) | Only if Google auth used |
| `DevAuth:Enabled` | Program.cs (dev bypass) | not set (false) | Must be false in production |

Placeholder validation (lines 22-41 of Program.cs) throws in production but only warns in development for: `AzureAd:ClientId`, `GraphApi:ClientSecret`, `Authentication:Local:SigningKey`.

---

## 7. Files Covered

**Backend**:
- `Controllers/AuthController.cs`
- `Controllers/DevAuthController.cs`
- `Controllers/LocalAuthController.cs`
- `Services/GraphApiService.cs`
- `Middleware/JwtValidationMiddleware.cs`
- `Middleware/DevelopmentAuthenticationHandler.cs`
- `Middleware/TenantClaimsMiddleware.cs`
- `Configuration/GraphApiOptions.cs`
- `Authentication/LocalJwtService.cs`
- `Authentication/GoogleTokenValidationOptions.cs`
- `Authorization/Permissions.cs`
- `Program.cs`

**Frontend**:
- `services/auth/authProvider.ts`
- `services/auth/microsoftAuthProvider.ts`
- `services/auth/googleAuthProvider.ts`
- `services/auth/localAuthProvider.ts`
- `services/auth/authFactory.ts`
- `services/auth/types.ts`
- `services/auth/index.ts`
- `services/auth/gis-types.d.ts`
- `hooks/useAuth.ts`
- `main.tsx`
- `services/api.ts`