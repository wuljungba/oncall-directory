# Security audit: onboarding, signup, and the access/permissions model

**Date:** 2026-08-25
**Scope:** New-customer onboarding, account signup, and the multi-tenant access/permissions model
**System:** ASP.NET Core 8 API + React/Vite SPA, Entra ID / Google / local auth, multi-tenant, HIPAA-relevant

---

## Why this audit happened

A brand-new Google account signed in and immediately had full "All Tenants" super-admin
access, with no `TenantAdmin` row and no entry in the admin UI's own signed-in-users list.

**That specific symptom was not a flaw in the Google, Entra, or tenant-claims code.** It was
`DevAuth:Enabled=true` in local development. When that flag is set, `Program.cs` never
registers the Microsoft, Google, or Local JWT bearer schemes at all: `DevelopmentAuthentication‑
Handler` becomes the only authentication scheme, discards whatever bearer token the browser
presents, and reads two cookies — defaulting `X-Dev-Role` to `"admin"`, which injects
`Admin.Full` and `Tenant.Manage` with no database lookup. `JwtValidationMiddleware` is skipped
off the same flag. The account never authenticated at all, which is also why
`TenantClaimsMiddleware.RecordSignIn` never recorded it.

The frontend already had `VITE_DEV_AUTH=false`, so it was sending a genuine Google token that
the backend silently threw away. **Nothing anywhere reported that mismatch.** That silence is
what turned a configuration detail into an apparent privilege-escalation bug.

Investigating it surfaced substantially worse problems than the one reported.

---

## Findings

Severity scale as specified: CRITICAL = cross-tenant or PHI exposure; HIGH = privilege
escalation; MEDIUM/LOW = UX or minor logic bugs.

### CRITICAL — all fixed

| # | Finding | Who could exploit it | Status |
|---|---|---|---|
| C1 | **Cross-tenant live code-call dispatch.** `POST /api/phone-trees/{treeId}/events` took the tree id from the route, created the incident, and called `DispatchIncidentAsync` with no check that the tree belonged to the caller's tenant — real SMS/voice paging of another customer's on-call clinicians. The `Confirm` flag is an operator-consent gate, not authorization. | any `Schedule.Write` holder (the ordinary scheduler permission) | Fixed |
| C2 | **Cross-tenant phone-tree tampering.** Reads were scoped; writes were not. `UpdatePhoneTreeAsync` was a bare `FindAsync` + `SetValues`, `AddNodeAsync` accepted any tree id, `CreatePhoneTreeAsync` accepted any department. Another hospital's code-blue escalation tree could be repointed or emptied so their next code call paged nobody. | any `Schedule.Write` holder | Fixed |
| C3 | **Phone-tree event leak.** `PhoneTreeEventService` contained no tenant filtering of any kind — every customer's live incidents, including `Location`, `LocationZone`, `Notes`, and participants. | any `Directory.Read` holder | Fixed |
| C4 | **SignalR fan-out.** 40 unconditional `Clients.All.SendAsync` calls across 8 files pushed every real-time event — staff changes, schedule changes, live code-call incidents — to every connected client of every customer. Group *membership* was gated correctly; the broadcast side ignored it. | any authenticated connected client | Fixed |
| C5 | **Compliance leak and cross-tenant write.** `DutyHourService` had no tenant filter and `departmentId` is optional, so omitting it returned every tenant's rules and swept every tenant's staff into the check. `CheckComplianceAsync` also *persists* `DutyHourViolation` rows, making this a write into another customer's compliance record. | any `Schedule.Read` holder | Fixed |

`Schedule.Read` and `Directory.Read` are the baseline permissions granted to everyone, and
`Schedule.Write` is the ordinary scheduler role. In practice these were reachable by nearly
every user of every customer.

### HIGH — all fixed

| # | Finding | Status |
|---|---|---|
| H1 | Single-item endpoints bypassed the filter their own list endpoints applied. `GET /api/departments/{id}` and `GET/PUT/DELETE /api/code-call-locations/{id}` read, rewrote, or retired any tenant's record by id; `Update` could also reassign a location into another tenant's department. | Fixed |
| H2 | `DevAuth:Enabled` had **no environment guard** — one config value disables all authentication and JWT validation in any environment, with no visible signal. `DevAuthController` was anonymous and mapped everywhere. | Fixed |
| H3 | No token revocation. `LocalAccount.IsActive` was checked only at sign-in, so a deactivated account kept full working access until its token expired — up to 24 hours. Roles were baked into the token and never re-read. | Fixed |
| H4 | `Hipaa:SessionTimeoutMinutes` was bound to nothing. The settings page wrote the value and no code ever read it; the HIPAA auto-logoff requirement was entirely cosmetic. | Fixed |

### MEDIUM / LOW — fixed

- **M1** — The SignalR hub joined `dept-{claim}` straight from a client-controlled token claim with no server-side check, while resolving tenant groups from the database precisely because a claim is not an authorization decision.
- **M2** — `JwtValidationMiddleware.ProtectedPrefixes` and `HipaaAuditMiddleware.AuditedPrefixes` had drifted: `/api/departments`, `/api/tenants`, `/api/escalation`, `/api/import`, `/api/messaging`, and `/api/code-call-locations` were audited as PHI-adjacent but skipped the scope/tenant checks. Not an authentication bypass — the bearer handler validates every token first — but an inconsistency across PHI routes.
- **M4** — The tenant request records carried no data annotations, so `[ApiController]` validation never fired: a blank or oversized name reached `SaveChanges` (500 on SQL Server, silently stored on SQLite). Tenant name uniqueness was also case- and whitespace-sensitive. Local registration did no email-format validation, which matters beyond hygiene because `TenantClaimsMiddleware` decides whether a `PermissionGrant` targets an email or an object id purely by whether it contains `@`.
- **M5** — **The frontend test suite could not run at all.** `@testing-library/react`, `jsdom`, and `jest-dom` were absent from `package.json` and `vite.config.ts` had no test block, while all three existing tests imported testing-library. All three were also stale: `Toast.test.tsx` imported a default export that no longer exists, and `AdminRoute.test.tsx` tested a component that had been deleted — its `vi.doMock` calls ran after import, so two of its three cases were vacuous and one asserted nothing.

### Verified sound — documented, pinned with tests, not changed

- **No SQL injection surface.** The auth and tenant paths are entirely LINQ-to-entities. The single `ExecuteSqlRawAsync` in `Program.cs` runs a fixed array of literal DDL with no user input concatenated.
- **JWT validation is correct** for all three providers: signature, issuer, audience, and lifetime. The Entra issuer validator correctly rejects the `common` endpoint and non-GUID tenant segments. No `ValidateLifetime=false` anywhere.
- **The frontend cannot spoof its auth provider.** `ForwardDefaultSelector` reads the token's real `iss` server-side, so `sessionStorage.authProvider` is a UI convenience with no security role.
- **The grant UI cannot mint an administrator.** `AssignablePermissions` excludes `Admin.Full`, `Admin.Scoped`, and `Tenant.Manage`, enforced by `ParseAssignablePermissionCsv`.
- **Audit logging captures denials.** `HipaaAuditMiddleware` runs before `UseAuthorization` and enqueues a row unconditionally, so 401s and 403s are recorded, not just successes.
- **SignalR group joins were already gated** (`JoinTenant`, `JoinDepartment`); broadcast was the gap.
- **Super admins already join every tenant group** on connect, which is what made removing `Clients.All` safe rather than blinding.
- **No self-service signup exists**, by design (see decisions below). There is no unauthenticated endpoint that can mint a tenant or an account.

---

## What was changed

All fixes reuse the existing correct pattern rather than inventing a new one:
`ITenantScope.AllowedTenantIdsAsync()`, which returns `null` for super admins and background
services and otherwise the caller's tenant list — where **an empty list must filter to
nothing**, the documented former fail-open bug. `DirectoryService.ScopePhoneTreesAsync` is the
reference implementation. The scope is a required constructor argument everywhere, so a wiring
mistake is a compile error rather than a silently unscoped query.

Out-of-tenant ids return **404, not 403**, so no endpoint confirms that another customer's
record exists.

Notable design decisions inside the fixes:

- **`ITenantBroadcaster`** replaces all 40 `Clients.All` calls. When a tenant cannot be resolved it does **not** fall back to broadcasting to everyone — that was a cross-tenant leak dressed as a delivery guarantee. It logs at Error and, on the safety-critical paths, writes a `NotificationUndeliverable` audit row, satisfying the no-silent-failure guardrail. The audit write is resolved leniently so a missing or failing audit sink can never take the dispatch path down with it.
- **Dev auth is now impossible to miss or to deploy.** Startup refuses to run with `DevAuth:Enabled` outside Development, `/api/auth/me` reports `authMode`, the UI shows a persistent banner, the handler logs once when it discards a real bearer token, and the dev endpoints return 404 when the mode is off.
- **Token revocation rejects an account that exists and is deactivated**, and refreshes roles from the database per request. A token whose account row is absent is not rejected: deactivation is the only removal path (`DELETE` is a soft delete), so an absent row means the token was never issued for a local account, and rejecting those would break legitimate token minting.

---

## Product and policy decisions

Four decisions were taken during planning. One rested on a premise that later proved wrong;
it is corrected here.

1. **Session timeout — build both halves.** The server now caps token lifetime to `Hipaa:SessionTimeoutMinutes`, and the client has an idle timer with a 60-second warning. Both were needed: a browser timer is advice, since a captured token can be replayed from a script.
2. **No self-service signup — confirmed intended.** A new customer cannot provision themselves: `TenantsController.Create` and `TenantAdminsController` require `Tenant.Manage`, and `LocalAuthController.Register` requires `Admin.Full`. Onboarding is admin-mediated. Confirmed there is no unauthenticated surface to attack.
3. **`OnCall.Admin` mapping — premise corrected.** The question was asked on the stated basis that anyone holding this Entra app role became a cross-tenant super admin. **That was wrong.** `RoleToPermissions` has exactly two references repo-wide: its definition and the dev-auth handler. No production code maps a role to a permission claim, and the role-based policies were deliberately removed earlier as "a weaker parallel authorization path." It was a loaded gun in a shared constant, not a live escalation path.
   **Consequence:** the "seed `Authentication:SuperAdmins` before shipping" production blocker is withdrawn — nothing revokes anyone's access.
   **What was done instead of the literal instruction:** the constant is renamed `DevRoleToPermissions` with an explicit "dev-mode only" comment, and a regression test pins that a *real* token carrying role `OnCall.Admin` receives no permissions at all. The dev handler still simulates a full admin, because simulating the highest-privilege user is the entire purpose of dev auth and it can no longer start outside Development. Scoping the dev role down as literally instructed would have removed tenant management from local development for no security gain.
4. **Entra-group auto-assignment no longer confers `CodeCall.Write`.** A new `AutoAssignedPermissions` set is `ScopedAdminPermissions` minus that one permission. Group membership is managed by IT and can change without anyone reviewing the individual; firing a live code call pages clinicians for a real emergency. It remains available through an explicit grant.

---

## Verification

| Suite | Before | After |
|---|---|---|
| Backend (`tests/BackendTests`) | 224 passing | **274 passing** |
| Frontend (`src/frontend`) | **could not run** | **23 passing** |
| `npm run lint`, `npm run build` | clean | clean |

Every new regression test was confirmed to **fail against the pre-fix code** by reverting each
fix and re-running — otherwise it pins nothing:

- C1/C3: 6 of 10 failed without the fix
- C2: 4 of 5 failed without the fix
- C5/H1: 3 failed without the fix
- H3: 2 failed without the fix

The pre-existing `CodeCallDispatch*Tests` were re-run and still pass, which was the specific
risk: the dispatch and escalation background services resolve the newly-scoped services outside
a request context, where the scope correctly reports "unrestricted."

**Safety note:** the code-call test factory explicitly disables every dispatch channel, because
`appsettings.Development.json` has Twilio **enabled** and these tests deliberately drive the
code-call path. Without that, a test run could have paged a real phone.

### New test files

`CodeCallTenantScopingTests`, `TenantBroadcastTests`, `DevAuthGuardTests`,
`TokenRevocationTests`, `InputValidationTests`, plus additions to `TenantScopingTests`,
`PermissionModelTests`, `LocalJwtServiceTests`, and frontend `useIdleTimeout.test.ts`,
`RouteGuards.test.tsx`, `useAuth.test.tsx`.

---

## Remaining risks and open items

### Needs your decision before production

1. **Azure App Service settings must be checked before the next deploy.** The new startup guard will **crash the app** if `DevAuth:Enabled` is set anywhere outside Development. That is the intended behaviour, but confirm it is absent or false on the production site **and every deployment slot** ahead of deploying, not by discovering it.
2. **`appsettings.Production.json`** — the `Hipaa` block is now live rather than decorative. `SessionTimeoutMinutes` is currently 15, which now genuinely caps local token lifetime and drives client idle logout. Confirm 15 minutes is the intended operational value before it reaches users.
3. **Token expiry** — `Authentication:Local:TokenExpiryMinutes` remains 1440 (24h) as the ceiling, now clamped by the session timeout. Worth deciding whether the raw ceiling should also come down.

### Known gaps, not fixed

- **`DutyHourRule` has no `TenantId`.** Rules are scoped through their department, which closes the department-specific leak, but a rule with no department is "organization-wide" — a notion predating multi-tenancy that has no owner, so those remain visible across tenants. They carry rule names and hour limits, no PHI. The real fix is a `TenantId` column plus a migration and a backfill decision for existing rows; the app uses `EnsureCreated` with idempotent DDL backports, so this is a schema change requiring production sign-off.
- **`TenantAdmin` has no `IsActive`.** Revoking a tenant admin still means deleting the row. Adding the column is a schema change, same gating as above.
- **Audit queue durability is unverified.** `IAuditService.Enqueue` is an in-memory channel. Whether entries survive a crash or shutdown matters for the six-year retention claim and has not been established.
- **SignalR hub invocations are not individually audited.** `/hubs` was not added to the audited prefixes because a WebSocket records once at disconnect; capturing per-invocation events (`JoinTenant`, incident acknowledgements) needs an `IHubFilter`.
- **Tenant groups are joined once at connect.** A tenant granted mid-session is not joined until the client reconnects.

### Still to do

**The live end-to-end persona walkthrough has not been run against a real OAuth sign-in.** The
automated suite exercises the same pipeline with dev auth off and real minted tokens, and covers
the full cross-tenant matrix, so the assertions are verified — but the actual Google/Entra
handshake needs a human at a browser. Two things are worth doing there:

1. Sign in with a genuine Google account against a backend running with `DevAuth:Enabled=false` and confirm it lands as a viewer with **no** tenants, and that it now appears in the Users & Permissions list — the two symptoms from the original report.
2. Open two browser sessions in different tenants, fire a code call in one, and confirm the other receives nothing. The invariant is pinned by `TenantBroadcastTests`, but a live two-client check is the honest confirmation.

Before either, confirm `Dispatch:Twilio:Enabled` is false and `StatusCallbackUrl` is unset so no
real page can leave the machine.

**Local development note:** the dev API process was stopped during this work to unblock builds
and needs restarting. Local dev auth still works exactly as before — the default `Development`
launch profile is unchanged — but it now shows a banner, and a new `Development-DevAuth` profile
names the bypass explicitly. `appsettings.Development.json` and `launchSettings.json` are
gitignored and untracked, so neither can reach a deployed environment.
