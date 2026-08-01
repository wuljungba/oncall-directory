# Super-Admin Access & Real Microsoft Entra Login

This guide covers two related pieces of production authentication:

1. **Super-admin full access** — designating specific real users as full administrators
   of the OnCall system, since real Entra/Google tokens carry no app roles.
2. **Real Microsoft Entra login** — wiring up actual Entra sign-in (SPA client +
   API scope + consent) instead of the dev-mode fake user.

---

## Part A — Super-Admin Full Access

### The problem

Real Entra ID and Google tokens carry **no application roles** (no `OnCall.Admin`,
`OnCall.Scheduler`, `OnCall.Viewer` claims) and no `Permission` claims. Role-based
`[Authorize]` policies and the permission policies (`Admin.Full`, `Tenant.Manage`,
etc.) would therefore deny *every* real signed-in user — there was no way for a
real user to reach admin functionality.

The super-admin feature fixes this: a user whose **email address** or **Entra
object ID** is listed in configuration is granted every role, every permission,
and `SuperAdmin` status on every active tenant by
[`TenantClaimsMiddleware`](../src/backend/OnCallApi/Middleware/TenantClaimsMiddleware.cs).

### Configuration

Section `Authentication:SuperAdmins` (bound to
[`SuperAdminOptions`](../src/backend/OnCallApi/Configuration/SuperAdminOptions.cs)):

```json
{
  "Authentication": {
    "SuperAdmins": {
      "Emails": ["it-admin@hospital.org"],
      "ObjectIds": ["00000000-0000-0000-0000-000000000000"]
    }
  }
}
```

- `Emails` — email addresses granted full access (matched case-insensitively
  against the token's `email` / `preferred_username`).
- `ObjectIds` — Entra object IDs granted full access (matched against the token's `oid`).
- Values come from environment variables / Key Vault (`Authentication__SuperAdmins__Emails__0`,
  `Authentication__SuperAdmins__ObjectIds__0`). They must **never** be committed
  to source control.

### What a super admin is granted

In [`Permissions.cs`](../src/backend/OnCallApi/Authorization/Permissions.cs):

- **Roles:** `OnCall.Viewer`, `OnCall.Scheduler`, `OnCall.Admin` (mirrors the
  dev-mode "admin" role set).
- **Permissions:** `Schedule.Read`, `Schedule.Write`, `Directory.Read`,
  `Directory.Write`, `CodeCall.Write`, `Admin.Scoped`, `Admin.Full`, `Tenant.Manage`.
- **Tenants:** a `TenantId:{id}` = `SuperAdmin` claim for every **active** tenant,
  so tenant-scoped APIs and `/api/auth/me` expose the full list.

### How it works (request pipeline)

Middleware order matters — tenant/super-admin claims are expanded **before**
authorization runs ([Program.cs](../src/backend/OnCallApi/Program.cs)):

```
UseAuthentication
  → (JwtValidationMiddleware, when DevAuth disabled)
  → TenantClaimsMiddleware   ← super-admin + tenant claims granted here
  → UseAuthorization         ← policies now see the granted claims
```

`TenantClaimsMiddleware`:
1. Matches the signed-in user against `SuperAdmins` (email or object ID).
2. If matched, `GrantSuperAdminAsync` adds all roles, all permissions, and
   `SuperAdmin` tenant claims — no `TenantAdmin` DB record is required.
3. If the DB is unavailable (migration not applied), the role/permission grant
   still succeeds; only the tenant-claim expansion is skipped.

### Verification

The behavior is covered end to end in
[`SuperAdminGrantTests`](../tests/BackendTests/Controllers/SuperAdminGrantTests.cs):

- A viewer-only user configured as a super admin gains `Admin.Full` + `Tenant.Manage`
  and can reach `GET /api/tenants` (which requires `TenantManage`).
- The same viewer **without** super-admin config is denied (403).
- A direct middleware test confirms roles, permissions, and `SuperAdmin` tenant
  claims are granted for active tenants only.

---

## Part B — Real Microsoft Entra Login

Use the dev-mode fake user for day-to-day work. When you need to test **real**
Entra sign-in (SSO, admin UX), follow this section. It uses a **single app
registration**: the SPA client ID doubles as the API audience.

### 1. Create / configure the Entra app registration

Azure Portal → App registrations → create an app (or reuse the existing "OnCall"
registration). Set the **single-app design** consistently:

| Value | Where it must match |
|-------|---------------------|
| **Application (client) ID** | `VITE_AZURE_CLIENT_ID`, `AzureAd:ClientId`, and the scope URI below |

1. **Application ID URI** → `Expose an API` → `api://<client-id>`.
2. **API scope** → `Expose an API` → add scope `access_as_user`
   (enable **Admin and user consent**).
3. **API permissions (optional Graph, delegated)** → the SPA no longer requests
   Microsoft Graph scopes (all Graph calls happen on the backend with app-only
   credentials). If the registration still lists them you may keep or remove them;
   they are no longer needed by the frontend.
4. **Grant admin consent** for `api://<client-id>/access_as_user` so silent token
   acquisition succeeds on fresh tenants (the interactive login also requests the
   scope, but pre-consent avoids the extra prompt).

### 2. Backend configuration

`appsettings.json` / Key Vault:

```json
{
  "AzureAd": {
    "Instance": "https://login.microsoftonline.com/",
    "Domain": "organizations",
    "TenantId": "organizations",
    "ClientId": "<your-real-client-id>",
    "Scopes": "api://<your-real-client-id>/access_as_user"
  }
}
```

`AzureAd:ClientId` and `AzureAd:Scopes` must reference the **same** client ID as
`VITE_AZURE_CLIENT_ID`. Microsoft.Identity.Web accepts the token whose `aud`
claim is `api://<client-id>` (its default `ValidAudience`), so no separate
`Audience` key is required.

**Disable dev auth** so real JWT validation runs:

```json
// appsettings.Development.json (or via ASPNETCORE_ENVIRONMENT=Production)
{ "DevAuth": { "Enabled": false } }
```

With DevAuth off, `DevelopmentAuthenticationHandler` is replaced by the
multi-provider JWT pipeline (Entra / Google / Local) and `JwtValidationMiddleware`
enforces the `access_as_user` scope.

### 3. Frontend configuration

Create `.env.local` (gitignored, takes precedence over `.env` — see
[`.env.local.example`](../src/frontend/.env.local.example)):

```
VITE_DEV_AUTH=false
VITE_AZURE_CLIENT_ID=<your-real-client-id>
VITE_GOOGLE_CLIENT_ID=
```

The MSAL provider now requests **only** the API scope
(`api://<client-id>/access_as_user`) for both the interactive login and silent
token acquisition ([`microsoftAuthProvider.ts`](../src/frontend/src/services/auth/microsoftAuthProvider.ts)).
There is no `@azure/msal-react` `MsalProvider` wrapper — MSAL is initialized
lazily by the auth factory — so no other wiring is required.

Delete `.env.local` to return to dev-mode auth.

### 4. Restoring dev mode

| Component | Setting | Normal value |
|-----------|---------|--------------|
| Frontend | `.env` `VITE_DEV_AUTH` | `true` |
| Frontend | `.env.local` | absent |
| Backend | `appsettings.Development.json` `DevAuth:Enabled` | `true` |

---

## Security notes

- Super-admin grants are **config-driven** and must come from Key Vault /
  environment variables, never hardcoded in source.
- The grant applies to the matching principal only; it does not affect other users.
- In production, `Authentication:Local:SigningKey` must be a real 32+ character
  secret — the app **fails fast at startup** if it is missing or short
  (see `LocalJwtService.GetSigningKey` and the `Program.cs` startup validation).
- Keep tenant separation intact: super admins see everything by design; everyone
  else stays scoped to their `TenantAdmin` assignments.
