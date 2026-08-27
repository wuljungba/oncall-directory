# OnCall Schedule & Directory — User Guide

**Status: DRAFT — not yet ready to be the official onboarding document.**
The app is not fully functional. Everything marked *Working* below was verified by driving
the running application on 2026-08-27. Everything in section 7 is a real defect or gap that a
new user will hit. Promote this to official once that section is empty.

---

## 1. What this application is

A multi-tenant on-call scheduling and phone directory system for hospitals. It answers three
questions:

- **Who is on call right now?** (Dashboard, On-Call Schedule)
- **How do I reach them?** (Phone Directory)
- **How do we page everyone at once in an emergency?** (Command Center, Phone Trees)

Around those sit staff administration, time-off approvals, duty-hour compliance reporting,
and a HIPAA audit trail.

"Tenant" in this app means **a hospital or facility** — it is the data isolation boundary. A
department (Cardiology, Emergency Medicine) sits inside a tenant.

---

## 2. Before you start

### Prerequisites
- .NET 8 SDK
- Node.js 18+
- No database setup needed locally: development uses a SQLite file created on first run.

### The one thing that confuses everyone: there are two authentication modes

The **frontend** and the **backend** each decide independently whether to use real sign-in or
development sign-in. **They must agree**, and as this repo currently stands they do not.

| | Setting | File |
|---|---|---|
| Frontend | `VITE_DEV_AUTH` | `src/frontend/.env` (true), overridden by `.env.local` (false) |
| Backend | `DevAuth:Enabled` | `src/backend/OnCallApi/appsettings.Development.json` (true) |

**Development mode** — `VITE_DEV_AUTH=true`, `DevAuth:Enabled=true`
You are signed in automatically as `dev@local` with full administrator rights. No Microsoft or
Google account needed. An amber banner reads *"DEVELOPMENT AUTH — you are not really signed
in."* Use this to explore the app.

**Real sign-in mode** — `VITE_DEV_AUTH=false`, `DevAuth:Enabled=false`
You sign in with Microsoft or Google. A brand-new account gets **no access at all** until an
administrator grants it. That is correct and expected, not a bug.

> **Do not mix the two.** With the frontend on real sign-in and the backend on dev auth, the
> backend ignores your token entirely and treats every request as a full administrator. This
> is exactly what once made a brand-new Google account appear to have "All Tenants"
> super-admin rights. A banner and a server log warning now make it visible, but the mismatch
> is still confusing.

To explore the app, set `VITE_DEV_AUTH=true` in `src/frontend/.env.local` (or delete that
file) and **restart the frontend** — Vite only reads env files at startup.

---

## 3. Starting the application

Two terminals. Backend first, because the frontend proxies to it.

```bash
# Terminal 1 — API on http://localhost:5000
cd src/backend/OnCallApi
dotnet run --urls "http://localhost:5000"

# Terminal 2 — web app on http://localhost:5173
cd src/frontend
npm install     # first time only
npm run dev
```

**Expected outcome:** `http://localhost:5173` loads. In development mode you land on the
Dashboard as `dev@local`. In real-sign-in mode you get a login screen offering Microsoft and
Google.

Verify the API independently:

```bash
curl http://localhost:5000/api/auth/me
```

This returns your identity, `permissions`, `tenantIds`, `authMode` (`development` or
`production`) and `sessionTimeoutMinutes`.

> **Before testing anything that pages people**, confirm dispatch is off. Twilio is
> **enabled with a real sender number** in `appsettings.Development.json`. The safe way to
> run locally:
> ```bash
> Dispatch__Twilio__Enabled=false dotnet run --urls "http://localhost:5000"
> ```

---

## 4. First-time setup

On first sign-in as an administrator, the **onboarding wizard** appears automatically.

| Step | What it asks | Expected outcome |
|---|---|---|
| 1 | Connect Microsoft 365, or choose **Local Only** | Local Only skips directory sync; you add staff by CSV or by hand |
| 2 | Import users | Confirms the directory is ready. The actual importing happens on the Phone Directory page |
| 3 | Create your first schedule | Creates a `Default` department if none exists, then a schedule. Toast: *"All Set!"* |

The wizard does not reappear once completed. Staff are added on the **Phone Directory** page,
not in the wizard.

---

## 5. Feature walkthrough

Navigation is the left sidebar. **Admin** appears only for administrators.

### 5.1 Dashboard — *Working*

The landing page. Shows **Currently On Call**, **Departments Covering**, **Directory
Entries**, your own upcoming coverage, and shortcuts to common tasks.

**Expected outcome:** counts reflect real data. With no shifts scheduled it reads *"No one is
currently on call"* — correct, not an error. If your account has no linked employee profile it
says *"No employee profile is linked to your account"* (see section 7).

### 5.2 Phone Directory — *Working*

The staff contact list, and the most-used page in the app.

| Action | Expected outcome |
|---|---|
| **Search** | Filters as you type across name, title, specialty, location, department and email. Apostrophes and long strings are handled safely |
| **Add Employee** | Creates the record; it appears in the list immediately |
| Add with a **duplicate email** | Red toast: *"Could not add employee — An employee with this email already exists."* |
| Add with an **invalid email**, a blank name, or a name over 100 characters | Rejected with a clear message |
| Phone entered as `(202) 555-0134` | Accepted, stored as `+12025550134` |
| Phone with an extension, e.g. `555-0134 x4412` | **Rejected.** An extension cannot be dialled, and merging it silently produced an undialable number |
| **Edit** | Saves; the list updates |
| **Download Template** | Downloads `employee-import-template.csv` with the nine expected headers |
| **Import CSV** | Opens the import dialog, with an optional dry-run validation pass |

**CSV import rules — read before importing.** The importer is CSV-only and requires **exact
camelCase headers**:

```
azureAdObjectId,firstName,lastName,email,title,officePhone,mobilePhone,officeLocation,departmentId
```

- `firstName`, `lastName` and `email` are required; the rest are optional.
- **All or nothing** — one bad row and nothing is imported.
- Re-importing the same people **updates** them rather than creating duplicates.
- Columns you leave out are untouched. A blank cell in a column you *do* include clears that value.
- Messy phone formats are fine: `(202) 555-0134`, `202-555-0134` and `+1 202 555 0134` all work.

Always start from **Download Template**. A file exported straight from an HR system almost
certainly will not import as-is — see section 7.

### 5.3 On-Call Schedule — *Backend verified; page renders*

Create schedules per department, generate rotations, assign shifts, handle swaps.

**Expected outcome:** creating a schedule requires a department. Generating shifts produces a
rotation with a **primary** clinician on call at every hour of the day, overnight included.
Shifts can be swapped, and a swap must be approved before it takes effect. A schedule can be
exported as `.ics` and subscribed to from Outlook or Google Calendar.

### 5.4 Command Center — *Page renders; dispatch deliberately not exercised*

The live emergency view: active incidents, dispatch progress, and who has acknowledged.

> Not exercised during testing, because starting a code call **pages real clinicians by SMS
> and voice**. Verify this feature only with dispatch channels disabled.

### 5.5 Phone Trees — *Page renders*

Escalation chains for emergencies: an ordered list of people to reach, each with a timeout
before the next is tried.

**Reachable only at `/dashboard/phone-trees`** — there is no sidebar link (section 7).

### 5.6 Time Off — *Backend verified*

Request time off; approve or deny as a manager.

**Expected outcome:** you cannot approve your own request, but your manager can. Approving
twice, denying after approval, or editing an approved request are each refused with an
accurate message.

### 5.7 Compliance — *Working, with one intermittent fault*

Duty-hour rules — maximum hours per period, minimum rest between shifts, maximum consecutive
days — and the violations found against them.

**Expected outcome:** the violations table lists breaches with employee, rule and date.
**Export** downloads a CSV of the current view.

The page occasionally returns a server error on load; reloading clears it (section 7).

### 5.8 Escalation — *Page renders*

Policies governing what happens when an on-call clinician does not acknowledge in time.
**Reachable only at `/dashboard/escalation`** — no sidebar link.

### 5.9 Settings — *Working*

Microsoft 365 integration status, notification toggles (Teams, email, SMS for escalations),
schedule defaults, and the **HIPAA session timeout** (default 15 minutes).

The session timeout is enforced in two places: the browser warns *"Still there?"* about a
minute before signing you out, and the server independently caps token lifetime to the same
value, so a captured token cannot outlive the policy.

### 5.10 Admin — *Working*

Administrators only.

| Tab | Purpose |
|---|---|
| Overview | Health summary and setup shortcuts |
| Accounts | Local accounts — create, deactivate |
| Departments | Create and manage departments |
| Integrations | Microsoft Graph connectivity diagnostics |
| Time Off | Organization-wide approvals |
| Code Call Locations | Locations selectable when starting a code call |
| Users & Permissions | Grant access to people who have signed in |
| Public Schedule | Share links showing coverage without names or numbers |
| Subscriptions | Tenants (hospitals/facilities) — super admin only |
| Onboarding | Setup health check |
| On-Call Audit | Historical coverage report with CSV export |

### 5.11 Public schedule links — *Backend verified*

A shareable read-only coverage link at `/on-call/{token}`, created under **Admin → Public
Schedule**.

**Expected outcome:** shows coverage counts only — **no names, phone numbers or email
addresses**. Unknown, disabled and expired tokens all return an identical response, so a link
cannot be probed to discover valid ones.

---

## 6. Who can do what

Permissions are granted per person, per tenant. A newly signed-in user has **none** until an
administrator grants them under **Admin → Users & Permissions**.

| Permission | Grants |
|---|---|
| `Schedule.Read` / `Schedule.Write` | View / edit schedules and shifts |
| `Directory.Read` / `Directory.Write` | View / edit the phone directory |
| `CodeCall.Write` | **Start a live code call.** This pages real clinicians |
| `Admin.Scoped` | Administer your own tenant |
| `Admin.Full` | Administer every tenant |
| `Tenant.Manage` | Create tenants and assign tenant administrators |

`Admin.Full`, `Admin.Scoped` and `Tenant.Manage` **cannot** be handed out through the
permissions UI, by design. They come only from the configured super-admin list or an explicit
tenant-admin assignment.

---

## 7. Known limitations

These need resolving before this document becomes official.

### Blocking for a new user

1. **A real HR export will not import.** The importer is CSV-only with exact camelCase
   headers. A file with `First Name` / `Work Email` / `Cell Number` fails with one error per
   row and never says the headers are the problem. An `.xlsx` renamed `.csv` — common, and
   true of two sample files we have on hand — is parsed as text and produces dozens of
   meaningless errors. Always start from **Download Template**.
2. **Frontend and backend auth modes ship mismatched** (`VITE_DEV_AUTH=false` against
   `DevAuth:Enabled=true`). Pick one mode before onboarding anyone.
3. **Phone Trees and Escalation have no sidebar link.** Two whole features are reachable only
   by typing the URL.

### Functional gaps

4. **Accounts created through the app cannot act as themselves.** An employee added without an
   Entra object id cannot acknowledge a shift, request time off, or request a swap — the app
   reports *"No employee profile is linked to your account."*
5. **Compliance intermittently returns a server error on load.** Reloading clears it. Caused by
   concurrent requests racing on the same rows, because the page writes data during a page view.
6. **Timestamps sent with a non-UTC offset are stored incorrectly**, off by the server's
   offset. The web app is unaffected because it always sends UTC; an EHR or mobile integration
   would not be.
7. **Presence sync cannot be switched off** and retries a misconfigured Microsoft Graph endpoint
   every minute, which has stalled the API under load. This contradicts the developer docs,
   which state sync is disabled in development.
8. **Escalation policies accept nonsensical values** — a negative response time causes every
   unacknowledged shift to escalate immediately.
9. **Bulk import writes no per-record audit entry.** Importing thousands of staff leaves a single
   generic log line, which is thin against a six-year HIPAA retention requirement.
10. **Settings are not tenant-scoped** — one tenant's setting keys are visible to another.
11. **Reversed and overlapping date ranges are accepted by the API** — a shift ending before it
    starts, or two overlapping time-off requests. The web app blocks these client-side.

### Not built yet

12. **Multi-file spreadsheet merge** — uploading several files, auto-matching columns and
    de-duplicating people is designed but not implemented.
13. **No self-service signup.** A new organization cannot create itself; an existing
    administrator must create the tenant and grant access. This is deliberate.

---

## 8. Troubleshooting

| Symptom | Cause and fix |
|---|---|
| Login screen when you expected to be signed in | The frontend is in real-sign-in mode. Set `VITE_DEV_AUTH=true` in `.env.local` (or delete the file) and **restart** the frontend |
| Signed in, but every page is empty and access appears pending | Correct for a new account with no permissions. An administrator grants access under **Admin → Users & Permissions** |
| *"Could not reach the server"* | The backend is not running, or not on port 5000 |
| Amber "DEVELOPMENT AUTH" banner | The backend is not checking credentials. Expected locally; it must never appear in a deployed environment |
| "Offline" badge in the header | The real-time connection (SignalR) is not connected. The app still works, but live updates will not arrive |
| Backend refuses to start, citing DevAuth | Intentional. `DevAuth:Enabled` is set outside development, which would disable all authentication |
| Import fails with *"firstName and lastName are required"* on every row | Wrong headers, or the file is really a spreadsheet renamed `.csv`. Start from **Download Template** |

---

## 9. Verification status

Verified on 2026-08-27 against the running application: all ten pages render without console
errors; the development-auth banner displays correctly at desktop and mobile widths with no
horizontal overflow; template download, the import dialog, employee creation, duplicate-email
rejection, input validation and phone normalization all behave as described here.

Backend behaviour for schedules, time-off, compliance, escalation, tenants and public share
links was verified through the API rather than the browser. The Command Center dispatch path
was deliberately not exercised, because it pages real people.

Test suites at time of writing: backend **315 passing**, frontend **30 passing**.
