# Fresh-tenant onboarding walkthrough — 2026-08-23

Local-only walkthrough of a brand-new customer standing up a tenant and using every
feature, driven as a genuinely fresh, unseeded identity rather than the seeded dev user.
Azure remains billing-disabled, so everything below ran against `localhost`.

**Scope note.** No PHI values appear in this report. Employee and patient records are
referenced by non-PHI identifiers (record id / tenant id) only.

---

## 1. Reproducible setup

### Servers

```bash
# backend — from src/backend/OnCallApi
dotnet run --urls "http://localhost:5000"      # health: GET /health  (NOT /api/health)

# frontend — from src/frontend
npm run dev                                    # port 5173, proxies /api and /hubs to :5000
```

**Frontend dev auth is not on by default.** `src/frontend/.env.local` (gitignored) sets
`VITE_DEV_AUTH=false` and overrides `.env`, so the SPA renders the real Entra login and
every page comes back as a ~179-character shell. To run this walkthrough the file must be
moved aside so `.env` (`VITE_DEV_AUTH=true`) applies, and Vite restarted. **Restore it
afterwards** — it carries the real SPA client id.

### The two dev endpoints added in Part 1

Both are inside the `DevAuth:Enabled=true` branch only; they do not exist in
Staging/Production and touch no JWT, encryption, audit, or session-timeout code.

- `POST /api/auth/dev/set-oid?oid={guid}` / `POST /api/auth/dev/clear-oid`
- `POST /api/auth/dev/set-role?role={viewer|scheduler|admin}` / `POST /api/auth/dev/clear-role`

### Bootstrap sequence (Part 2) — all 7 steps passed

Run with a cookie jar (`Invoke-WebRequest -SessionVariable`):

| # | Call | Result |
|---|------|--------|
| 1 | `POST /api/auth/dev/set-oid?oid=00000000-0000-0000-0000-000000000099` | 200 |
| 2 | `POST /api/auth/dev/set-role?role=admin` | 200 |
| 3 | `GET /api/auth/me` | 200, `id=...0099`, `tenantIds: []` |
| 4 | `POST /api/tenants` `{"name":"Test Onboarding Co", ...}` | 201, **tenantId = 3** |
| 5 | `POST /api/tenants/3/admins` `{"azureAdObjectId":"...0099","role":"SuperAdmin"}` | 201 |
| 6 | `POST /api/auth/dev/set-role?role=viewer` | 200 |
| 7 | `GET /api/auth/me` | 200 — see below |

Step 7 produced exactly the intended scoped-admin shape, with nothing leaking in from the
seeded tenant:

```json
{"id":"00000000-0000-0000-0000-000000000099",
 "roles":["OnCall.Viewer"],
 "permissions":["Schedule.Read","Directory.Read","Schedule.Write",
                "Directory.Write","CodeCall.Write","Admin.Scoped"],
 "tenantIds":[3],"tenantRoles":{"3":"SuperAdmin"}}
```

**Tenant numbering correction:** the seeded dev tenant `"Test"` is **tenant 2**, not 1.
Tenant 1 is `"Main Hospital"` and currently has no admin rows.

---

## 2. Findings

Severity: **P0** safety-critical / **P1** blocks a real user / **P2** correctness or
scope defect / **P3** minor.

| # | Area | Severity | Status |
|---|------|----------|--------|
| F1 | Onboarding cannot complete for a tenant's first admin | P1 | **Fixed** |
| F2 | `GET /api/compliance/check` 500s under concurrency | P1 | **Fixed** |
| F3 | All-channels-failed code call had no aggregate alarm | P0 | **Fixed** |
| F4 | Incident detail endpoint hid the dispatch timeline | P2 | **Fixed** |
| F5 | Compliance endpoints leak employee data across tenants | P1 | Flagged |
| F6 | Phone-tree incident endpoints leak across tenants | P1 | Flagged |
| F7 | Code-call locations: global-by-default + unscoped read | P2 | Flagged |
| F8 | Settings are global; scoped admins cannot write any | P2 | Flagged |
| F9 | Tenant SuperAdmin cannot read or manage own tenant | P2 | Flagged |
| F10 | Scoped admin cannot review time off | P2 | Flagged |
| F11 | `npm run build` is broken on `main` | P2 | Flagged |
| F12 | Dev identity display names collide | P3 | Flagged |
| F13 | Email stored in an `AzureAdObjectId` column is inert | P3 | Flagged |
| F14 | Product gap: no self-service tenant onboarding | — | Flagged (§4) |

### Fixed in this session

#### F1 — A tenant's first admin could never finish onboarding (P1)

`PUT /api/settings/{key}` requires `RequireAdminFull` (`SettingsController.cs:36`). A
fresh tenant's first admin holds `Admin.Scoped`, never `Admin.Full`, so the wizard's
completion write returns **403** — confirmed against `onboarding.completed:3`.

That write sat inside the same `try` as schedule creation in
`OnboardingWizard.handleStep3`, so the 403 was caught by the schedule handler. Observed
behaviour before the fix:

1. Step 3 created the `Default` department and the schedule — both succeeded (dept 13, schedule 2).
2. The settings write 403'd and threw.
3. The user was told **"Could not create schedule. You can do this later."** — false; it had been created.
4. `onComplete()` never ran, so neither the server flag nor the localStorage mirror was set.
5. The wizard stayed open. Every retry created **another duplicate schedule**.

Fix: give the completion write its own `try/catch`, matching the tolerance `handleSkip()`
already applies and the failure mode `dismiss()` already documents. Setup success and
flag-persistence are now reported independently.

Verified in a real browser: the wizard now shows "All Set!", never the false schedule
error, and does not reappear after reload. The underlying 403 still occurs — that is F8.

#### F2 — `GET /api/compliance/check` 500s when it races itself (P1)

A plain GET performs a destructive read-modify-write: it loads the un-resolved violation
set, `RemoveRange`s it, inserts a freshly computed set, and saves
(`DutyHourService.CheckComplianceAsync`). Two overlapping requests both load the same
rows; the first commits its deletes; the second then deletes rows that are already gone
and EF raises `DbUpdateConcurrencyException` → **500**.

Reproduced deterministically — sequential requests returned 200/200, four concurrent
requests returned 200/200/200/**500**. The Compliance page fires this on load, so two
tabs or a reload mid-flight is enough.

Fix: a row another writer already deleted is not a conflict worth failing on — the
desired end state has been reached. Conflicting *deleted* entries are detached and the
insert is committed (bounded retry). Only all-deleted conflicts are absorbed; anything
else still throws. `ExecuteDeleteAsync` would be tidier but is relational-only and the
test suite runs on the InMemory provider.

Verified: six concurrent requests now all return 200.

#### F3 — A code call that reached nobody was logged as routine (P0, safety-critical)

The known P0 — *zero channels configured reported as success* — **is already fixed**:
that branch records `acknowledged/failed` with "DISPATCH FAILED … Escalate by phone now",
logs at Error, and deliberately leaves the event unacknowledged.

Its sibling case was not. When every *configured* channel fails and the SIP fallback does
not carry it, the pipeline emitted only per-channel `failed` steps and then logged, at
**Information** level, `"Dispatch pipeline complete for event 8: 0/1 channels succeeded"`.
No aggregate failure step, no operator instruction, and wording that reads as success for
a dispatch that contacted no one.

This is the case that actually occurs here: Twilio is the only enabled channel and the
account is a trial that rejects every code-call body (error 572006), so every real code
call lands in this branch.

Live evidence — code call fired on the fresh tenant's own tree (event 8, tree 7):

```
[skipped] cucm_axl_check :: CUCM integration not configured
[skipped] informacast    :: InformaCast integration not configured
[skipped] vocera         :: Vocera integration not configured
[failed]  twilio_sms     :: No on-call provider mobile number on file for this event
[failed]  sip_fallback   :: SIP fallback not configured — manual dispatch required
                            <-- nothing else; pipeline logged at Information
```

Fix: give this branch the same aggregate alarm as its zero-channel sibling — an
`acknowledged/failed` step reading "DISPATCH FAILED — every dispatch channel failed, so
nobody was contacted. Escalate by phone now.", the event left active and unacknowledged,
and the pipeline summary logged at Error.

Verified (event 9): the aggregate step is present and the log line is now
`fail: … Dispatch pipeline finished for event 9: 0/1 channels succeeded — nobody contacted`.

The **confirm gate works**: `POST /api/phone-trees/{id}/events` without `confirm:true`
is rejected 400, so no accidental dispatch.

#### F4 — Incident drill-down hid the dispatch timeline (P2)

`GetEventByIdAsync` omitted the `.Include(e => e.DispatchSteps)` that
`GetActiveEventsAsync`/`GetResolvedEventsAsync` both have, so
`GET /api/phone-trees/events/{id}` returned an **empty** dispatch history while the list
endpoints returned a populated one — hiding exactly the failures an operator opens a
specific incident to see. Fix: add the include. Verified: 0 steps → 9 steps.

### Flagged — not fixed here

#### F5 / F6 / F7 — Cross-tenant data leaks (P1, HIPAA-relevant)

Confirmed by direct observation as the tenant-3 scoped admin, whose own tenant contains
no staff and no incidents:

- **`ComplianceController` has no tenant scoping at all.** `GET /api/compliance/check`
  returned full employee records belonging to another tenant — including name, work
  email, office and mobile numbers, title and specialty. Same for `/rules`,
  `/check/{employeeId}`, `/hours/{employeeId}`. This is the most serious leak found.
- **`GET /api/phone-trees/events/resolved`** returned another tenant's code-call incident
  history, including the tree and procedure text — while `GET /api/phone-trees` is
  correctly scoped and returned `[]`. The scoping is inconsistent within one controller.
- **`CodeCallLocationsController`**: `GetAll` treats any location with no department as
  global, so the fresh tenant sees another organisation's physical code-call locations and
  could dispatch to them. `Get(id)` applies **no** tenant check at all.

**Why these are flagged rather than patched now.** Each fix is an authorization-filter
change on the PHI and code-call paths. The guardrails call for explicit sign-off there,
and a half-applied scoping change on the dispatch path is more dangerous than a
documented one. These want a scoped change reviewed by `hipaa-compliance`, with the
super-admin path and the "null department/tenant means global" convention settled
deliberately — that convention is the root cause of F7 and probably F8.

#### F8 — Settings are global and unwritable by scoped admins (P2)

`AppSetting` carries a `TenantId` column that is never used for filtering. `GET
/api/settings` returns every tenant's keys to any reader; `PUT` requires `Admin.Full` and
can overwrite any key including another tenant's. This is the direct cause of F1's 403.
Fixing it properly means scoping the table by tenant *and then* letting a scoped admin
write their own tenant's keys — again an authorization change needing sign-off.

#### F9 / F10 — A tenant SuperAdmin cannot administer their own tenant (P2)

For the fresh tenant's SuperAdmin:

- `GET /api/tenants`, `/api/tenants/3`, `/api/tenants/3/admins` → **403**
  (`TenantsController` and `TenantAdminsController` are gated on `RequireTenantManage`,
  which `Admin.Scoped` does not grant). They cannot view their own tenant record or invite
  a second admin. The Directory page fires `/api/tenants` on load and logs a 403.
- `GET /api/schedule/time-off/review` and `/time-off/all` → **403**. The Time Off page
  surfaces this as a failed request; the tenant's own admin cannot approve or deny
  requests. `/time-off/me` works.
- Also 403 for a scoped admin: `/api/admin/onboarding/health`, `/api/admin/shares`,
  `/api/audit/on-call-report`.

Whether these should be reachable by `Admin.Scoped` is a permissions-model decision, not
a bug to patch unilaterally. It is the same root as F14.

#### F11 — `npm run build` is broken on `main` (P2)

`tsc` fails with 10 errors before Vite runs. Confirmed pre-existing: identical failures
on a clean `HEAD` with all of this session's changes stashed.

- `@testing-library/react` is imported by three test files but is not in `package.json`.
- `src/pages/AdminRoute.test.tsx` imports `./AdminRoute`, which does not exist.
- `Toast.test.tsx` imports a default export `Toast.tsx` does not have.
- Jest-DOM matchers (`toBeInTheDocument`, `toHaveClass`) have no type declarations.

`npm run test` cannot pass either, for the same missing dependency. `npm run lint` is
clean. Either add the missing dev dependencies and the `AdminRoute` component, or exclude
test files from the production `tsconfig`.

#### F12 / F13 — Minor

- `DevelopmentAuthenticationHandler.cs:55` derives the dev display name from
  `oidCookie[..8]`, so every OID of the form `00000000-…` becomes `dev-00000000@local`.
  Because `TenantClaimsMiddleware.AddPermissionGrantsAsync` matches permission grants by
  email as well as object id, two distinct dev identities would share email-keyed grants.
  Dev-only. Using the last segment, or the full OID, would fix it.
- Tenant 2 has a `TenantAdmin` row whose `AzureAdObjectId` column holds an email address.
  Claim expansion matches that column against the OID claim only, so the row grants
  nothing and is silently inert. Pre-existing data, not created by this walkthrough.

#### Operational note (not a code finding)

`appsettings.Development.json` is gitignored and correctly excluded from deployment, but
holds live Twilio account credentials in plaintext on disk. Startup also warns that
`Dispatch:Twilio:StatusCallbackUrl` is unset, so **SMS delivery failures cannot be
detected locally** — a code-call SMS that Twilio accepts and then fails to deliver will
sit recorded as `completed`.

---

## 3. Verification run

| Check | Result |
|-------|--------|
| `dotnet build` | Succeeded, 0 warnings |
| `dotnet test` (`tests/BackendTests`) | **224 passed**, 0 failed |
| `npm run lint` | Clean |
| `npm run build` | Fails — pre-existing (F11), unrelated to these changes |
| Manual repro: compliance concurrency | 6/6 concurrent requests now 200 |
| Manual repro: all-channels-failed code call | Aggregate failure step present; logged at Error |
| Manual repro: incident detail | 0 → 9 dispatch steps |
| Manual repro: onboarding wizard | Completes, correct toast, stays dismissed after reload |
| Regression: seeded dev user | Unaffected — see below |

### Regression check

After `clear-oid` + `clear-role`, the seeded identity resolves as before: OID `…0001`,
`dev@local`, `Admin.Full` + `Tenant.Manage`, `tenantIds: [2]`, `tenantRoles {2:SuperAdmin}`.
Tenant 2's admin rows are unchanged, still carrying the `…0001` SuperAdmin row created
before this session. Nothing in the seeded tenant was modified.

---

## 4. Product gap: no self-service tenant onboarding

**There is no code path anywhere in the application — dev or production — by which a new
user can become the administrator of a new tenant without an existing administrator, or
`SuperAdminOptions` configuration, acting first.** This was confirmed by reading the
source, and every observation in this walkthrough is consistent with it.

- `TenantsController` and `TenantAdminsController` are both gated on the
  `RequireTenantManage` policy, which needs the `Tenant.Manage` permission claim.
- In production that claim originates only from `SuperAdminOptions` config or from an
  existing `TenantAdmin` row. `TenantClaimsMiddleware` states this outright: configured
  super administrators are *"the only way a real user can obtain Admin.Full /
  Tenant.Manage today."*
- `LocalAuthController.Register` is itself gated on `RequireAdminFull`, so a prospective
  customer cannot even self-register an account.
- The remaining automatic path — Entra group mapping — is an invitation by construction:
  an administrator must map a group and add the user to it.

The walkthrough could only proceed by using the dev `X-Dev-Role=admin` cookie to grant
`Tenant.Manage` transiently for steps 4 and 5 — standing in for the administrator who
would have to exist in production. That is a simulation of provisioning, not self-service.

F9 sharpens the point: even *after* provisioning, the new tenant's SuperAdmin cannot read
their own tenant record or add a second admin, because those endpoints require
`Tenant.Manage` and a scoped admin never has it. A single-admin tenant has no in-app way
to add a colleague.

This is a genuine product gap, not a defect to patch. Closing it means a deliberate
decision about tenant provisioning — self-serve signup with verification, an invitation
flow, or an operator-run process — and a matching decision about what `Admin.Scoped`
should be allowed to do within its own tenant. It should not be closed by loosening any
existing authorization check.

---

## 5. Test data and cleanup

Created by this walkthrough, all under **tenant 3 `"Test Onboarding Co"`** (named
unambiguously so it is safe to remove):

| Object | Id |
|--------|-----|
| Tenant `Test Onboarding Co` | 3 |
| TenantAdmin (OID `…0099`, SuperAdmin) | 3 |
| Department `Default` | 13 |
| Schedule `Primary Call` | 2 |
| Schedule `Primary Call Rotation` | 3 |
| PhoneTree `Onboarding Test Code Blue` | 7 |
| PhoneTreeEvents (+ their DispatchSteps) | 8, 9 |
| SignInIdentity (OID `…0099`) | 4 |

**The local database is LocalDB SQL Server, not SQLite** —
`Server=(localdb)\mssqllocaldb;Database=OnCallDb`. `DELETE /api/tenants/{id}` is a *soft*
delete (it only sets `IsActive = false`), so removing this data needs SQL. Run against
`OnCallDb`, in this order:

```sql
DELETE FROM DispatchSteps      WHERE PhoneTreeEventId IN (8, 9);
DELETE FROM PhoneTreeEvents    WHERE Id IN (8, 9);
DELETE FROM PhoneTrees         WHERE Id = 7;
DELETE FROM Shifts             WHERE ScheduleId IN (2, 3);
DELETE FROM Schedules          WHERE Id IN (2, 3);
DELETE FROM DutyHourViolations WHERE EmployeeId IN (SELECT Id FROM Employees WHERE DepartmentId = 13);
DELETE FROM Employees          WHERE DepartmentId = 13;
DELETE FROM Departments        WHERE Id = 13;
DELETE FROM TenantAdmins       WHERE TenantId = 3;
DELETE FROM Tenants            WHERE Id = 3;
DELETE FROM SignInIdentities   WHERE ExternalObjectId = '00000000-0000-0000-0000-000000000099';
```

Leaving the data in place is also fine — it is inert and scoped to tenant 3.

### State restored at the end of the session

- `src/frontend/.env.local` restored byte-identical (verified with `diff`).
- Both dev servers stopped — Vite had stale env after the restore.
- Dev cookies are per-browser and were only ever set in throwaway Playwright contexts;
  `DevelopmentAuthenticationHandler` defaults to the seeded OID `…0001` when no cookie is
  present, so a fresh browser is already the seeded identity. `clear-oid` / `clear-role`
  were exercised and both return 200.
- The seeded `"Test"` tenant (2) and the `…0001` admin row were not touched.

### Files changed

- `src/frontend/src/components/OnboardingWizard.tsx` — F1
- `src/backend/OnCallApi/Services/DutyHourService.cs` — F2
- `src/backend/OnCallApi/Services/CodeCallDispatchService.cs` — F3
- `src/backend/OnCallApi/Services/PhoneTreeEventService.cs` — F4
