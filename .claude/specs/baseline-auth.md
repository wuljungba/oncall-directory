# Baseline: Authentication & Graph Integration Discovery Report

**Date**: 2026-07-31 (refreshed after Entra end-to-end audit; original 2026-07-30)
**Scope**: Identity & Graph Integration Specialist
**Files examined**: All backend auth controllers, middleware, Graph service, auth config, plus frontend auth providers, useAuth hook, main.tsx, api.ts, signalr.ts, CI/CD workflow, Bicep infra, deployment guide.

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
UseAuthentication() / UseAuthorization()   -- invokes JWT bearer handler(s)
  |
  v
JwtValidationMiddleware (skipped in dev mode)   -- Program.cs:427-432
  |
  v
TenantClaimsMiddleware                           -- Program.cs:435
  |
  v
HipaaAuditMiddleware                             -- Program.cs:439
  |
  v
Controller / Endpoint
```

### 1a. Multi-Provider JWT Routing (Program.cs, lines 58-208)

The backend does NOT use a single JWT handler. It registers three schemes and routes by `iss` claim:

| Issuer | Scheme | How it validates |
|--------|--------|------------------|
| `login.microsoftonline.com/{tenant}/v2.0` | `Bearer` (default) | via `Microsoft.Identity.Web` 2.18.0 + custom `IssuerValidator` |
| `https://accounts.google.com` | `"Google"` | `AddJwtBearer` with Google OIDC authority, JWKS auto-resolved |
| `oncall-directory` | `"Local"` | Symmetric HMAC-SHA256 via `LocalJwtService.GetValidationParameters()` |

The **ForwardDefaultSelector** (Program.cs:177-206) reads each JWT's `iss` claim at runtime and routes to the matching scheme. Unparseable tokens fall back to the Microsoft handler.

### 1b. Microsoft Entra ID Validation (Microsoft.Identity.Web)

The default `"Bearer"` scheme is configured via `AddMicrosoftIdentityWebApi(builder.Configuration.GetSection("AzureAd"))` (Program.cs:71), pulling from `appsettings.json`:

```json
"AzureAd": {
  "Instance": "https://login.microsoftonline.com/",
  "Domain": "organizations",
  "TenantId": "organizations",
  "ClientId": "your-api-client-id",
  "Scopes": "api://your-api-client-id/access_as_user"
}
```

A **PostConfigure** step (Program.cs:139-207) overrides `ValidIssuer` with a custom `IssuerValidator` that:
- Accepts any Azure AD tenant issuer (`login.microsoftonline.com/{tid}/v2.0` where tid is a real GUID)
- Rejects `common` (and any non-GUID segment)
- The same PostConfigure replaces `Events.OnTokenValidated`, chaining the Microsoft.Identity.Web handler, and adds `auth_provider: microsoft`

Audience validation: `Microsoft.Identity.Web` 2.18.0 defaults `TokenValidationParameters.ValidAudience` to `api://{ClientId}` when `AzureAd:Audience` is not set. So with `AzureAd:ClientId` set to the SPA's client ID, a token with `aud = api://<clientid>` IS accepted — no separate `Audience` key needed. (Recommendation for the fix spec: set `AzureAd:Audience: api://<client-id>` explicitly to remove ambiguity.)

Multi-tenant note: `TenantId: "organizations"` makes the authority `https://login.microsoftonline.com/organizations/v2.0`. Tokens still carry the user's actual tenant GUID as issuer, which the custom validator accepts. The JwtValidationMiddleware additionally requires `tid` != "common".

### 1c. Required Claims Checked by JwtValidationMiddleware

`JwtValidationMiddleware` (Middleware/JwtValidationMiddleware.cs) enforces on protected prefixes (lines 32-41: `/api/directory`, `/api/schedule`, `/api/phone-trees`, `/api/compliance`, `/api/settings`, `/api/integrations`, `/api/admin`):

1. Authenticated (else 401)
2. `scp` contains `access_as_user` OR `auth_validated=true` (lines 77-92, else 403)
3. `oid`/`sub`/NameIdentifier present (lines 94-103, else 401)
4. For `auth_provider` null or "microsoft": `tid` present and != "common" (lines 106-119, else 401)

Note: `/api/auth/*`, `/api/tenants`, `/api/escalation`, `/api/import`, `/api/code-call-locations` are NOT in the prefix list — they rely solely on `[Authorize]` policies, not the scope gate.

### 1d. Authorization Policies (Program.cs, lines 210-268)

Role-based policies use `ClaimTypes.Role`: `RequireViewer` = OnCall.Viewer/Scheduler/Admin (line 218), `RequireScheduler` (215), `RequireAdmin` (212). Permission-based policies use the `Permission` claim (`Schedule.Read`, `Schedule.Write`, `Directory.Read`, `Directory.Write`, `Admin.Full`, `Admin.Scoped`, `Tenant.Manage`, `CodeCall.Write`).

**Critical for Entra**: an MSAL access token contains `roles` ONLY if the user is assigned app roles in the app registration. The `/api/auth/me` endpoint requires `RequireViewer` (AuthController.cs:21). An Entra user with no assigned app roles gets 403 everywhere, including `/api/auth/me`. Unlike Google (which auto-adds `OnCall.Viewer` at Program.cs:103) and Local (roles baked into the JWT), Microsoft users have NO fallback role.

### 1e. Where Tenant Context Comes From

`TenantClaimsMiddleware` (Middleware/TenantClaimsMiddleware.cs:22-148): looks up `TenantAdmin` records by the user's `oid`, adds `TenantId:{id}` claims + scoped permission claims, and lazy auto-assigns `DepartmentAdmin` when a user's `groups` claim matches a `Tenant.AzureAdGroupId`. Graceful degradation on DB errors (lines 57-64). Runs for every authenticated request (registered at Program.cs:435 after JwtValidationMiddleware).

### 1f. AuthController `/api/auth/me`

Returns Id/Name/Email/Roles/Permissions/TenantIds/TenantRoles from claims (AuthController.cs:20-51).

---

## 2. Dev Mode Bypass

Two independent switches: backend `DevAuth:Enabled: true` (appsettings.Development.json:2-4) and frontend `VITE_DEV_AUTH=true` (src/frontend/.env:15).

- Backend: registers only `DevelopmentAuthenticationHandler` (Program.cs:49-56); the entire multi-provider JWT pipeline is SKIPPED; `JwtValidationMiddleware` is not added (Program.cs:427-432). Fake claims (scp=access_as_user, fake oid/tid, all roles, `TenantId:1`) are injected (DevelopmentAuthenticationHandler.cs:49-77).
- Frontend: `main.tsx` skips MSAL entirely (main.tsx:56-58); `useAuth` pre-seeds a fake user and short-circuits signIn/signOut/refresh (useAuth.ts:44-51, 121-132, 156-177); `api.ts` reads `sessionStorage.accessToken` (api.ts:45).
- Production: `DevAuth__Enabled: false` is set by Bicep (main.bicep:190, 222). appsettings.Production.json does not set DevAuth. Confirmed production has no dev bypass in the template.

The two switches are independent — dev mode masks the entire Entra path. Everything in section 7 below only manifests when BOTH are turned off.

---

## 3. GraphApiService Authentication to Microsoft Graph

### 3a. Authentication Method

`GraphApiService` (Services/GraphApiService.cs:24-50) uses **app-only** `ClientSecretCredential`:

```csharp
var creds = new ClientSecretCredential(
    _options.Value.TenantId,
    _options.Value.ClientId,
    _options.Value.ClientSecret);
_client = new GraphServiceClient(creds, _options.Value.Scopes);
```

`GraphApiOptions.Scopes` (Configuration/GraphApiOptions.cs:16) defaults to `["https://graph.microsoft.com/.default"]` — i.e., "all permissions granted to the app registration". `appsettings.json` does not override it. So effective scopes = whatever admin-consented app permissions the registration has.

Lazy init: `_client` created on first call; `_clientInitialized` flag prevents retries after failure. Startup health check at Program.cs:391-416 calls `CheckGraphConnectionAsync()` and logs a warning (does not crash).

### 3b. Actual Graph API Operations (Permissions Actually Used in Code)

| Operation | Graph API endpoint | Required Entra app permission |
|-----------|-------------------|------------------------------|
| List users | `GET /users` | `User.Read.All` or `Directory.Read.All` |
| Users delta | `GET /users/delta` | `User.Read.All` or `Directory.Read.All` |
| Get presence | `GET /users/{id}/presence` | `Presence.Read.All` |
| List chats | `GET /users/{id}/chats` | `Chat.ReadBasic.All` |
| Send chat message | `POST /chats/{id}/messages` | `ChatMessage.Send` |
| Create calendar event | `POST /users/{id}/calendar/events` | `Calendars.ReadWrite` |
| List groups | `GET /groups` | `Group.Read.All` |
| Group members | `GET /groups/{id}/members` | `GroupMember.Read.All` or `Group.Read.All` |
| SharePoint page | `POST /sites/{id}/lists/SitePages/items` | `Sites.ReadWrite.All` |

Because `.default` is used, any app permission granted beyond this list is silently included (over-provisioning risk). Flag any extra permission granted on the GraphApi registration.

### 3c. Two Separate App Registrations

- **`AzureAd`** = the SPA/API registration (user sign-in, MSAL audience)
- **`GraphApi`** = server-side app-only registration (ClientSecretCredential)

They must be DIFFERENT registrations with different client IDs. The single-app-registration design for the SPA (same clientId used as API audience) is intentional and covered in section 7.

---

## 4. Google and Local Auth Placement

### 4a. Google Auth

Frontend (`googleAuthProvider.ts`): GIS credential flow. `VITE_GOOGLE_CLIENT_ID` configures it. The ID token (credential) is stored as `accessToken` and sent as `Authorization: Bearer` to the backend. **Has a silent refresh** (`performSilentRefresh`, lines 175-204) via `initTokenClient({prompt: ''})` with single-flight dedupe (line 167).

Backend (Program.cs:74-108): named `"Google"` scheme, authority `https://accounts.google.com`, audience validated against `Authentication:Google:ClientId`, `OnTokenValidated` adds `auth_provider: google`, `auth_validated: true`, `oid: google-{sub}`, and a default `OnCall.Viewer` role. Google users are ALWAYS Viewer — no promotion path.

### 4b. Local Auth

`LocalAuthController.cs` (register/login/change-password/reset) + `LocalJwtService.cs`: HMAC-SHA256, issuer `oncall-directory`, audience `oncall-api`, 24h expiry, dev fallback signing key at line 118 (guarded). `Authentication:Local:SigningKey` placeholder validated at Program.cs:41. Frontend `localAuthProvider.ts` stores the JWT in sessionStorage, no refresh (expiry forces re-login).

### 4c. Frontend Provider Selection

`authFactory.ts`: `getAuthProvider(type?)` reads `sessionStorage.authProvider`, defaults to `microsoft`, caches instances in a module-level Map. `clearProviders()` called on sign-out. `getAllProviders()` is dead code (no callers). Provider is chosen by which login UI the user clicks (LoginPage.tsx SSO buttons/local form).

---

## 5. The Two-MSAL-Instances Problem (New finding, verified 2026-07-31)

**Verdict: `main.tsx` creates a SECOND, separate `PublicClientApplication` purely to feed `<MsalProvider>`; NO component consumes `@azure/msal-react` context; the wrapper is dead weight, not harmful today, but it is an unsupported configuration.**

Evidence:
- `main.tsx:61-62` `const msalProvider = new MicrosoftAuthProvider()` → `getMsalInstance()` → line 47 `<MsalProvider instance={msalInstance}>`.
- `authFactory.ts:26` creates ANOTHER `new MicrosoftAuthProvider()` (cached in the module Map) when `getAuthProvider('microsoft')` is first called by `useAuth` (useAuth.ts:97, 137-139), `api.ts:48`, `services/auth.ts:34-36`, and `useAuth.refreshToken` (useAuth.ts:172).
- Grep for `useMsal|MsalAuthenticationTemplate|AuthenticatedTemplate|useIsAuthenticated|useAccount|MsalProvider` across `src/frontend/src` returns ONLY main.tsx:4, main.tsx:47, and the doc comment in microsoftAuthProvider.ts:52. No component reads MSAL React context.

Which instance drives what:
- Login / API tokens / refresh / SignalR: the `authFactory` singleton (instance #2).
- The `main.tsx` instance (#1) only initializes and renders the inert wrapper.

Where state can diverge: both instances share `cacheLocation: 'sessionStorage'` (microsoftAuthProvider.ts:14-16). MSAL's sessionStorage keys are shared across instances. When a user signs in via instance #2's `loginPopup`, instance #1's active account (set at init from whatever account was cached) can go stale. MSAL docs do not support two `PublicClientApplication` instances over one cache. Today it's harmless only because instance #1 never performs token operations.

Fix direction for the spec: delete the `MsalProvider` wrapper + instance in main.tsx, drop `@azure/msal-react` (package.json:16), and initialize the authFactory singleton once.

## 6. MSAL Token Request Issues (New finding, verified 2026-07-31)

- `TOKEN_REQUEST` (microsoftAuthProvider.ts:30-35) mixes `api://<clientid>/access_as_user` with `https://graph.microsoft.com/User.Read` in ONE token request. An access token has a single audience; the graph scope can never appear in the API token. Verified `@azure/msal-common` 14.16.1 does NOT reject multi-resource scope sets at request validation (RequestValidator has no resource check), so the request goes to the STS with mixed `scope` params — either erroring (silent step throws) or minting a token for the first resource only. The graph scope in TOKEN_REQUEST is semantically wrong and must be removed (the frontend never calls Graph directly — grep confirms the only `graph.microsoft.com` reference is this scope string).
- `LOGIN_REQUEST` (lines 20-28) requests five Microsoft Graph delegated permissions (User.Read, User.ReadBasic.All, Calendars.ReadWrite, Presence.Read.All, OnlineMeetings.ReadWrite) at the login popup. The SPA never uses Graph, so this only forces a consent screen and requires those delegated permissions be declared on the app registration. Recommend reducing LOGIN_REQUEST to `openid profile` + the api:// scope.
- **Login consent gap (likely hard failure on fresh app registrations)**: `loginPopup(LOGIN_REQUEST)` does NOT include `access_as_user` (the API scope). After a successful popup, `signIn()` calls `acquireTokenSilent(TOKEN_REQUEST)` for the api:// resource. If the tenant has not admin-pre-consented `access_as_user`, the silent step throws InteractionRequiredAuthError, the catch returns null (microsoftAuthProvider.ts:117-119), and `useAuth` never sees a user — "login did nothing" even though MSAL cached an account. The only interactive path that requests the api:// scope is the private `signInPopup` fallback (lines 194-205), reachable only from `getAccessToken()` after the fact. Fix: include `api://<clientid>/access_as_user` in the interactive login request (or require admin pre-consent).

## 7. End-to-End Entra Login: What Works and What Breaks in Real Mode

### 7a. Chain coherence once configured

Frontend `loginPopup` → token with `aud = api://<clientid>`, `scp = access_as_user`, `oid`, `tid` → backend `ForwardDefaultSelector` routes to `Bearer` (Microsoft) → Microsoft.Identity.Web validates signature/audience (`api://{ClientId}` default)/issuer (custom validator) → JwtValidationMiddleware passes scp/oid/tid → TenantClaimsMiddleware adds tenant claims → `[Authorize]` policies gate endpoints. **The validation chain itself is coherent.**

### 7b. Breakages in real mode (in dependency order)

1. **Placeholder client IDs (hard)** — `.env` has `VITE_AZURE_CLIENT_ID=your-spa-client-id`; code fallback is `your-api-client-id` (microsoftAuthProvider.ts:10); backend `AzureAd:ClientId=your-api-client-id`. MSAL popup → AADSTS700016 (application not found). Nothing works until a real client ID is set on all three.
2. **CI build bakes the placeholder (hard)** — `.github/workflows/deploy.yml:58-60` runs `npm run build` with NO `VITE_AZURE_CLIENT_ID`/`VITE_DEV_AUTH` env vars. Vite inlines `import.meta.env.VITE_AZURE_CLIENT_ID` at build time from the committed `.env`, so the deployed SPA always contains the placeholder client ID. The Bicep `AzureAd__ClientId` app setting is irrelevant to the frontend build.
3. **Mixed-scope token request + consent gap (likely hard)** — section 6.
4. **No roles on Entra tokens (hard for all authorized endpoints)** — Entra access tokens contain `roles` only when the user is assigned app roles on the registration. `RequireViewer` fails with 403 otherwise. Every user needs at least the `OnCall.Viewer` app role (or the backend must add a fallback role for Entra, as it does for Google at Program.cs:103).
5. **App registration/authority mismatch (medium)** — the deployment guide registers `--sign-in-audience AzureADMyOrg` (single-tenant), but the code uses the `organizations` authority (multi-tenant). Either align the authority to the tenant GUID or change sign-in audience to `AzureADMultipleOrgs`.
6. **Production config gaps (hard at deploy)** — `appsettings.Production.json` sets `AzureAd:ClientId`, `GraphApi:ClientSecret`, `Authentication:Local:SigningKey`, `GraphApi:TenantId/ClientId` to EMPTY strings. This DEFEATS the `ValidateSecret` placeholder guard (Program.cs:39-41), because empty != placeholder. The Bicep template wires only `AzureAd__*`, `Cors__Origin`, `ApplicationInsights__ConnectionString`, `Redis__*`, `Storage__*` (main.bicep:179-190) — it does NOT set `GraphApi__*`, `Authentication__Local__SigningKey`, or `Authentication__Google__ClientId`. So a stock production deploy runs with empty GraphApi/Google/Local config and silent failures. The deployment guide's table (deployment-guide.md:124-142) lists these as Key Vault, but nothing in Bicep reads them.
7. **`parameters.production.json` placeholders** — Key Vault resource ID `/subscriptions/your-subscription/...` (lines 14, 21, 28, 36) must be replaced.

### 7c. Required Entra app registration configuration (user checklist)

One registration (single-app design — SPA client ID doubles as the API audience):

1. **Platform**: Add "Single-page application" platform.
2. **Redirect URIs** (must EXACTLY match `window.location.origin`, microsoftAuthProvider.ts:12 — no trailing slash):
   - `http://localhost:5173` (dev popup flow)
   - `https://app-oncall-production.azurewebsites.net` (prod)
   - `https://app-oncall-production-staging.azurewebsites.net` (staging)
3. **Expose an API**: Application ID URI = `api://<client-id>`; add scope `access_as_user` (admin + user consent enabled). Must match `AzureAd:Scopes` in appsettings.json.
4. **API permissions (Microsoft Graph, delegated)**: whatever remains in LOGIN_REQUEST (currently User.Read, User.ReadBasic.All, Calendars.ReadWrite, Presence.Read.All, OnlineMeetings.ReadWrite) — ideally these are removed from the code instead of granted. Grant admin consent for `access_as_user` (or ensure the login request includes it interactively).
5. **App roles** in the manifest: `OnCall.Viewer`, `OnCall.Scheduler`, `OnCall.Admin` (deployment-guide.md:53-59), and assign at least `OnCall.Viewer` to every user/group that should log in.
6. **Tenant/authority**: either keep `organizations` authority + `AzureADMultipleOrgs` sign-in audience (matches the code's multi-tenant issuer validator and its `tid != common` check), or set `AzureAd:TenantId`/authority to the single tenant GUID and `AzureADMyOrg`.
7. **Client IDs**: set the same real client ID in `VITE_AZURE_CLIENT_ID` (build-time env, not committed `.env`), `AzureAd:ClientId`, and `AzureAd:Scopes` (`api://<id>/access_as_user`).

Separate **GraphApi** registration (app-only): grant the app permissions in section 3b, admin-consent, and feed `GraphApi:TenantId/ClientId/ClientSecret` from Key Vault/App Service settings (currently NOT wired in Bicep).

## 8. Placeholder Inventory (Entra path)

| Location | Key | Placeholder |
|----------|-----|-------------|
| src/frontend/.env:7 | VITE_AZURE_CLIENT_ID | `your-spa-client-id` |
| src/frontend/.env:11 | VITE_GOOGLE_CLIENT_ID | `your-google-client-id.apps.googleusercontent.com` |
| src/frontend/.env:15 | VITE_DEV_AUTH | `true` (must be `false` for real mode) |
| src/frontend/src/services/auth/microsoftAuthProvider.ts:10 | code fallback | `your-api-client-id` (inconsistent with `.env`) |
| src/frontend/.env.example | VITE_AZURE_CLIENT_ID | `your-spa-client-id` |
| src/backend/OnCallApi/appsettings.json:16 | AzureAd:ClientId | `your-api-client-id` |
| src/backend/OnCallApi/appsettings.json:17 | AzureAd:Scopes | `api://your-api-client-id/access_as_user` |
| src/backend/OnCallApi/appsettings.json:21 | Authentication:Google:ClientId | `your-google-client-id.apps.googleusercontent.com` |
| src/backend/OnCallApi/appsettings.json:24 | Authentication:Local:SigningKey | `change-me-to-a-32-char-min-secret-key!!` |
| src/backend/OnCallApi/appsettings.json:29-31 | GraphApi:TenantId/ClientId/ClientSecret | `your-home-tenant-id` / `your-graph-client-id` / `your-graph-client-secret` |
| src/backend/OnCallApi/appsettings.json:46 | ApplicationInsights:ConnectionString | `""` |
| src/backend/OnCallApi/appsettings.Production.json:13-31 | AzureAd/Google/Local/GraphApi | `""` (empty — defeats the ValidateSecret guard) |
| infrastructure/bicep/parameters.production.json:14,21,28,36 | Key Vault resource ID | `/subscriptions/your-subscription/...` |
| .github/workflows/deploy.yml:58-60 | VITE_AZURE_CLIENT_ID / VITE_DEV_AUTH | not passed at all |

ValidateSecret guard (Program.cs:22-41) is effectively neutered in production by the empty-string overrides in appsettings.Production.json — recommended fix: also reject empty values, or keep placeholders.

## 9. Gaps / Fragilities (consolidated)

1. Two MSAL instances over a shared sessionStorage cache — section 5.
2. Multi-resource token request + consent gap — section 6.
3. Entra tokens carry no role claim by default — 403 on all authorized endpoints until app roles are assigned.
4. `/api/auth/me` not covered by JwtValidationMiddleware's prefix list (JwtValidationMiddleware.cs:32-41).
5. Graph app permissions not wired in Bicep; empty production values defeat the placeholder guard.
6. `AzureAd` multi-tenant (`organizations`) vs. deployment guide single-tenant registration.
7. Production `VITE_*` env not injected in CI build → placeholder client ID baked into the deployed bundle.
8. SignalR hub (`[Authorize]` only, OnCallNotificationHub.cs:10) is validated by Microsoft.Identity.Web but bypasses the JwtValidationMiddleware scp/tid gate (path not in prefix list) — acceptable today because API-audience tokens always carry access_as_user, but worth noting.

## 10. Files Covered

**Backend**: Controllers/AuthController.cs, DevAuthController.cs, LocalAuthController.cs; Services/GraphApiService.cs; Middleware/JwtValidationMiddleware.cs, DevelopmentAuthenticationHandler.cs, TenantClaimsMiddleware.cs; Configuration/GraphApiOptions.cs; Authentication/LocalJwtService.cs, GoogleTokenValidationOptions.cs; Hubs/OnCallNotificationHub.cs; Program.cs; appsettings.json/.Development.json/.Production.json; OnCallApi.csproj (Microsoft.Identity.Web 2.18.0, Microsoft.Graph 5.57.0).

**Frontend**: services/auth/* (authProvider.ts, types.ts, authFactory.ts, index.ts, microsoftAuthProvider.ts, googleAuthProvider.ts, localAuthProvider.ts), services/auth.ts, services/api.ts, services/signalr.ts, hooks/useAuth.ts, hooks/useSignalR.tsx, main.tsx, App.tsx, pages/LoginPage.tsx, LandingPage.tsx, .env, .env.example, vite.config.ts, package.json.

**Infra/CI/docs**: infrastructure/bicep/main.bicep, infrastructure/bicep/parameters.production.json, .github/workflows/deploy.yml, docs/deployment-guide.md.
