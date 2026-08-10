# OnCall — Employee Onboarding Standard

This is the single source of truth for how a person enters the OnCall directory and
becomes able to sign in. Every path — Azure AD sync, CSV import, manual add, local
account — must satisfy the same two standards:

1. **Source classification** — every `Employee` record is tagged with where it came from.
2. **Account + permissions baseline** — every person who needs to sign in has a linked
   identity and a baseline permission to see/work the on-call schedule.

Two things this standard deliberately does **not** require (they're optional on the
record): a `Manager` (falls back to admin for time-off approval) and a `Department`.

---

## 1. Source classification

Every `Employee.Source` value means one, and only one, thing:

| Value | Meaning | Set by | Auto-deactivated by AD sync? |
|-------|---------|--------|------------------------------|
| `Ad` | Created/confirmed by the Azure AD sync (Entra identity) | `AdSyncBackgroundService` | **Yes** (only if the AD user disappears) |
| `CsvImport` | Bulk CSV import (`Import Employees`) | `BulkImportService` | No — never |
| `Local` | Manually created (Admin → Accounts → Add Employee, or onboarding) | `AdminService` | No — never |
| `" "` (empty) | Legacy/migrated record | any pre-standard path | No — treated as local |

**Rules**

- Records created by the AD sync are the **only** ones that can ever be auto-deactivated.
  A locally-created record is never automatically deactivated, no matter what its
  `AzureAdObjectId` happens to hold. (This is enforced in `AdSyncBackgroundService`.)
- CSV imports of people who are **not** in Entra should leave the `azureAdObjectId`
  column **blank** — the importer assigns the safe `csv-import-*` id and `Source=CsvImport`.
- There is no valid reason to edit `Source` by hand; if you believe a record's origin is
  wrong, delete it and re-add via the correct path.

## 2. Account + permissions baseline

For a person to sign in and be useful, they need **both**:

1. **A sign-in identity.** Pick exactly one:
   - **Entra**: their `Employee.AzureAdObjectId` matches the Entra object id they sign in
     with (AD sync provides this automatically).
   - **Local account**: create a Local Account (admin → Users & Permissions → Local Accounts)
     with the **same email** as the `Employee`. Optionally link `employeeId` to the record.
2. **A baseline permission** so their session actually grants access (admin → Users &
   Permissions → *Grant on-call permission to a user*), using `Schedule.Read` + `Directory.Read`
   as the floor for a normal user.

| Person type | Identity | Baseline permission |
|-------------|----------|---------------------|
| Staff (performs on-call) | Entra link **or** local account | `Schedule.Read`, `Directory.Read` |
| Scheduler | staff baseline | + `Schedule.Write`, `CodeCall.Write` |
| Department/tenant admin | Entra link (tenant-admin row) | `Admin.Scoped` (per tenant) |
| System admin | Entra link (super-admin config) | `Admin.Full`, `Tenant.Manage` |

**Rule:** no `Employee` should exist for a person without a sign-in identity *and* at least
`Schedule.Read`. If you only want a directory entry (no login), `Source=CsvImport` with no
identity/permission is acceptable — that's a directory-only entry.

## 3. Path runbooks

### A. Azure AD-managed (recommended for real staff)
1. Ensure Entra is configured (Graph sync healthy — see `graph-auth-troubleshooting`).
2. Person is added in Entra; within one sync interval the `Ad` record appears, linked by
   `AzureAdObjectId`; presence/calendar follow automatically.
3. Grant grantable permissions if they exceed the baseline.

### B. CSV import (people not in Entra, or bulk upload)
1. Use the directory's **Download Template**.
2. Leave `azureAdObjectId` blank for anyone not in Entra (blank → safe `csv-import-*` + `CsvImport`).
3. Import (`Directory → Import CSV`). `Source=CsvImport` — never auto-deactivated.
4. If they should sign in: create a **Local Account** with their email, and grant the
   baseline permission. Otherwise it's a directory-only entry.

### C. Manual add (Admin → Accounts → Add Employee)
1. Fill name/email/title (+ optional dept/manager).
2. `Source` is set to `Local` automatically.
3. Same as B(4): link a sign-in identity if needed and grant a baseline permission.

### D. Local account sign-in
- Admin → Users & Permissions → Local Accounts → **Add**, matching the employee's email.
- Role/permission: start with viewer; promote via the permissions tab.

## 3.5 Department & manager (optional enrichment)

- **Department**: optional per the standard, but when a department matters for
  scheduling, set it. CSV imports support a `departmentId` column (import into the right
  department the first time); manual adds pick it in the form. Schedules rotate per
  department, and the directory groups by it.
- **Manager**: optional per the standard — if unset, time-off approval falls back to an
  admin. Set `ManagerId` (Admin → Accounts → Edit) to route approvals to that person's
  manager view and to power the direct-reports enrichment.

## 4. Post-onboarding verification

After adding a person, confirm (in the app, not just the UI):

- The directory shows them (hard-refresh — imports don't auto-refresh before the recent fix).
- They can sign in (if they should) with the chosen identity.
- They can open the schedule and read the directory (baseline permission held).
- For time-off: their requests route to a manager if one is set, otherwise to an admin.

_Where this is enforced in code:_ `Employee.Source` (model + deactivation guard in
`AdSyncBackgroundService`), `BulkImportService` (`Source=CsvImport`), `AdminService`
(`Source=Local`). The permission baseline is enforced administratively via the Users &
Permissions tab (there is no hard block — the standard is the process).