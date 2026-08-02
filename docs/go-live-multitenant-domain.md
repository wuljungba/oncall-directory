# Go-Live Plan: Multi-Tenant Sign-In + Custom Domain

This is the working go-live plan for shipping the on-call system to production
with **true multi-tenant** Microsoft Entra sign-in and a **custom web domain**.

## Current state (verified 2026-08-01)

| Area | Status |
|------|--------|
| Subscription | `Azure subscription 1` (`9c06e9c9-…`) |
| Resource group | `rg-oncall-production` (westus3) — **production only, no staging RG** |
| Deployed | SQL Server + DB, Redis, Storage, Key Vault, App Insights, Log Analytics |
| **Not deployed** | **App Service Plan + web app + staging slot** (defined in `main.bicep`, never deployed) |
| Entra app | "OnCall API" (`96955ba3-…`) — SPA redirects fixed, roles fixed to `OnCall.*`, service principal provisioned |
| Entra sign-in audience | `AzureADMyOrg` (**single-tenant**) |
| Entra custom domain | none (only `yisadivinyahoo.onmicrosoft.com`) |
| Code | frontend authority `organizations`, backend issuer validator accepts any `login.microsoftonline.com/<tenant-guid>` issuer |

---

## Phase 0 — Deploy the missing App Service

The Bicep defines `appPlan` + `webApp` + `stagingSlot`, but the current RG was
deployed from an earlier/partial run. Deploy the full template (or just the
`Microsoft.Web` resources) so there is a live URL to bind the domain to:

```bash
az deployment group create \
  --resource-group rg-oncall-production \
  --template-file infrastructure/bicep/main.bicep \
  --parameters environmentName=production \
      sqlAdminPassword='<secret>' \
      entraTenantId='<your tenant id>' \
      entraClientId='96955ba3-c70c-4205-8637-a4b34301480a' \
      entraDomain='yisadivinyahoo.onmicrosoft.com' \
      corsOrigin='https://<your-custom-domain>' \
      location=westus3
```

Note: `main.bicep` sets `DevAuth__Enabled=false` and wires
`AzureAd__ClientId`/`Cors__Origin` from parameters. `sqlAdminPassword` and the
`GraphApi__*`/`Authentication__Local__SigningKey` values must be supplied (Key
Vault or App Service settings) — see `docs/deployment-guide.md`.

**Recommendation:** also create a staging RG (`rg-oncall-staging`) and deploy the
same template with `environmentName=staging`, so the swap-based pipeline
(`.github/workflows/deploy.yml`) has a real staging target.

---

## Phase 1 — True multi-tenant sign-in

Goal: any authorized hospital organization signs in with **its own** Entra tenant.

### 1a. Registration changes

1. **Sign-in audience** — change `AzureADMyOrg` → `AzureADMultipleOrgs`:
   ```bash
   az ad app update --id 96955ba3-c70c-4205-8637-a4b34301480a \
     --set signInAudience=AzureADMultipleOrgs
   ```
2. **App roles** stay as-is (`OnCall.*`). Each customer tenant's admin assigns
   them to their users; token `roles` claims will carry the matching values.
3. **Consent** — each customer tenant admin must consent once, via the admin
   consent URL:
   `https://login.microsoftonline.com/<customer-tenant-id>/adminconsent?client_id=96955ba3-c70c-4205-8637-a4b34301480a`
   (No consent is needed for the app's *own* `access_as_user` scope — see
   `docs/superadmin-and-entra-login.md`.)

### 1b. Code is already compatible

- Frontend authority `organizations` → resolves to any org tenant. ✓
- Backend issuer validator accepts any `login.microsoftonline.com/<guid>`
  issuer (rejects `common`). ✓
- `JwtValidationMiddleware`/policies are audience/scope based. ✓

### 1c. Tenant resolution by Entra tenant ID — **implemented**

`TenantClaimsMiddleware` now resolves the tenant from the token's `tid` claim
against `Tenant.AzureAdTenantId` (the approved-tenant allow-list):

- A user whose `tid` matches an **active** tenant's `AzureAdTenantId` is
  auto-assigned `DepartmentAdmin` (like group-membership assignment) and gets the
  scoped permission set + a `TenantId:{id}` claim.
- Users from **unapproved or inactive** tenants get no tenant claims and are
  denied by default (no data access).
- The existing `oid`-based (`TenantAdmin` records) and group-based resolution
  remain as fallbacks, so tenants without `AzureAdTenantId` keep the legacy
  single-tenant behavior.
- Super admins bypass the allow-list entirely.
- Covered by tests in `tests/BackendTests/Services/TenantAllowListTests.cs`.

**Schema note:** the repo has no EF migrations (schema is `EnsureCreated`-based),
so `Tenant.AzureAdTenantId` is included automatically for fresh databases. An
**existing** SQL Server database needs the column added manually:

```sql
ALTER TABLE Tenants ADD AzureAdTenantId nvarchar(100) NULL;
```

Then populate it per customer tenant (or via the Tenant admin UI once exposed).

**Remaining:** expose `AzureAdTenantId` in the Tenant admin UI for managing the
allow-list at runtime.

### 1d. Security / HIPAA decisions to make

- **Open vs. approved tenant list.** `AzureADMultipleOrgs` lets *any* org tenant
  attempt sign-in once they consent. For a HIPAA-bound system, recommend either:
  - Keep multi-tenant open but gate all data access behind `Tenant` resolution +
    `RequireAdminFullOrScoped` (a user with no matching tenant gets nothing), **or**
  - Add a **tenant allow-list** (the app rejects `tid` claims not in `Tenant.AzureAdTenantId`).
- **SaaS super-admins** stay config-driven via `Authentication:SuperAdmins`
  (emails/object IDs), independent of customer tenants. ✓ already implemented.
- **Audit/HIPAA** (`HipaaAuditMiddleware`, session timeout, audit retention) are
  tenant-agnostic and apply to all sign-ins. ✓
- **Code-call/escalation path** must remain scoped per tenant; verify no
  cross-tenant dispatch can occur once tenant resolution lands.

---

## Phase 2 — Custom domain for web access

The web app is a SPA+API on a single App Service (`https://app-oncall-production.azurewebsites.net`).
Bind a custom hostname like `https://app.<yourdomain>`.

### 2a. Choose the domain

You must own a domain and control its DNS. Options:
- **Subdomain of a domain you own** (e.g. `app.oncall.<yourdomain>`): simplest.
- **New registered domain** (purchase at any registrar — Namecheap, Cloudflare,
  GoDaddy, etc.): requires payment/account on your side.

### 2b. DNS + hostname binding (once domain is confirmed)

```bash
# 1. DNS (at your DNS provider): CNAME app.<yourdomain> -> app-oncall-production.azurewebsites.net

# 2. Bind to the App Service (and the staging slot):
az webapp config hostname add --webapp app-oncall-production \
  --resource-group rg-oncall-production --hostname app.<yourdomain>
az webapp config hostname add --webapp app-oncall-production --slot staging \
  --resource-group rg-oncall-production --hostname staging-app.<yourdomain>

# 3. Free managed TLS cert (auto-rotating):
az webapp config ssl bind --certificate-thumbprint <thumbprint> \
  --ssl-type Sni --webapp app-oncall-production \
  --resource-group rg-oncall-production --hostname app.<yourdomain>
```

### 2c. Entra + app updates for the domain

1. **Redirect URIs**: add `https://app.<yourdomain>` (and the staging host) as
   SPA redirect URIs on the "OnCall API" registration.
2. **CORS**: set `Cors__Origin` to `https://app.<yourdomain>` (Bicep param).
3. **Entra custom domain** (optional, for user sign-in under your domain instead
   of `…onmicrosoft.com`): verify the domain in Entra ID (add + DNS TXT/MX), then
   add user accounts. This is separate from the *web* domain and not required to
   serve the app.
4. **MSAL `redirectUri`**: the frontend uses `window.location.origin`, so it picks
   up the custom domain automatically once served there.

---

## Phase 3 — Deploy the app

With infra + domain in place, the GitHub Actions pipeline
(`.github/workflows/deploy.yml`) builds → tests → publishes → deploys to staging →
health check → swaps to production. Steps:
1. Push `main`.
2. Confirm the workflow's `AZURE_CLIENT_ID`/secrets and `environmentName`
   parameters point at the right resource group.
3. Verify `/health` on the staging slot before swap.

---

## What I can run vs. what you must do

| Step | Who | Status |
|------|-----|--------|
| Add `Tenant.AzureAdTenantId` + middleware `tid` resolution | Claude (code + tests) | ✅ Done (no EF migration — repo is EnsureCreated-based) |
| Change sign-in audience to multi-tenant | Claude (with your OK) | ⏳ Ready to run |
| Deploy missing App Service infra | Claude | 🔴 **Blocked: subscription throttles App Service Plan creation** (`infrastructure/bicep/app-service.bicep` ready to re-run once un-throttled or on another sub) |
| Add hostname binding + managed cert | Claude (after DNS exists) | ⏳ Deferred (no custom domain yet) |
| **Register/purchase a domain** | **You** (registrar account + payment) | ⏳ Deferred |
| **Create DNS records** | You (or Claude if DNS is reachable) | ⏳ Deferred |
| Verify custom domain in Entra (optional) | Claude/you | ⏳ Deferred |

## Decisions (as of 2026-08-01)

1. **Web domain** — none yet; ship on `app-oncall-production.azurewebsites.net`,
   add a custom domain later.
2. **Multi-tenant posture** — **approved tenant allow-list** (implemented via
   `Tenant.AzureAdTenantId` + `tid` resolution). ✔
3. **App Service deploy** — attempted; the subscription rejected it
   (*"App Service Plan Create operation is throttled for subscription"*). Needs a
   quota lift/other subscription before the app can go live.
4. **Staging** — deploy a `rg-oncall-staging` for the swap pipeline, or go
   straight to production with the single slot? *(still open)*
