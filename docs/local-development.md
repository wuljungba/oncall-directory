# Running OnCall locally

A local instance that behaves closely enough to production to test against — including **real
Microsoft sign-in**, which works even when the Azure subscription is unavailable, because
Entra ID has no billing dependency.

## Two modes

| Mode | Sign-in | Use it for |
|------|---------|-----------|
| **DevAuth** (default) | None — every request is a fake all-roles admin | Quick UI work |
| **Real SSO** (`scripts/run-local-backend.sh`) | Actual Microsoft redirect | Anything auth-shaped |

DevAuth bypasses the JWT pipeline entirely, so with it on you **cannot** test permissions,
tenant scoping, delegated sub-admins, or the signed-in-users directory. Use real SSO for those.

## Start it

```bash
./scripts/run-local-backend.sh
```

PowerShell: `.\scripts\run-local-backend.ps1`

Then, in a second terminal:

```bash
cd src/frontend && npm run dev
```

API on `http://localhost:5000`, app on `http://localhost:5173` (Vite proxies `/api` and
`/hubs` to the API). **Start the API first** — the frontend proxy expects it.

Confirm real auth is actually on before signing in:

```bash
curl -s -o /dev/null -w "%{http_code}\n" http://localhost:5000/api/auth/me
```

**`401` is correct.** A `200` means DevAuth is still enabled and the setup silently did
nothing — every subsequent test would pass for the wrong reason.

Then open `http://localhost:5173`, click **Sign in with Microsoft**, and you should land on
the dashboard with an **Admin** tab. The script sets you as a super admin; without that you
would sign in successfully and land on the *"You're signed in — access pending"* panel,
because a real Entra token carries no app roles.

## The stale-database trap

`src/frontend` aside, the local database is a **SQLite file in the build output**
(`src/backend/OnCallApi/bin/Debug/net8.0/OnCallDb.sqlite`).

`EnsureCreated` builds the schema from the model, but it is a **no-op once any table exists**,
and the idempotent DDL backport in `Program.cs` is T-SQL and runs only on SQL Server. So a
local database created before a model change **never receives that change**. The symptom is a
runtime `no such table` / `no such column`, which reads like a code bug.

The fix is to delete it and let it rebuild:

```bash
rm src/backend/OnCallApi/bin/Debug/net8.0/OnCallDb.sqlite
```

The API logs a warning at startup when it skips the backport, naming this. If you see
features failing on missing tables or columns, delete the file first.

### The same trap catches the test suite

`tests/BackendTests` keeps its **own** copy of the file, and the integration tests boot the
real application over it. After any model change, expect failures that look nothing like the
change you made — the ones that surfaced this were two SignalR hub-negotiate tests returning
`500`, because `ExceptionHandlingMiddleware` hides the detail from the response. The real
cause is in the test log: `SqliteException: no such column`.

Delete every copy, not just the API's:

```bash
rm -f tests/BackendTests/bin/Debug/net8.0/OnCallDb.sqlite       tests/BackendTests/obj/testrun/Debug/net8.0/OnCallDb.sqlite       src/backend/OnCallApi/bin/Debug/net8.0/OnCallDb.sqlite       src/backend/OnCallApi/obj/testrun/Debug/net8.0/OnCallDb.sqlite
```

A related trap when writing tests: build the in-memory database name **once** and capture it.

```csharp
var dbName = Guid.NewGuid().ToString();                        // correct
services.AddDbContext<AppDbContext>(o => o.UseInMemoryDatabase(dbName));

services.AddDbContext<AppDbContext>(                            // wrong
    o => o.UseInMemoryDatabase(Guid.NewGuid().ToString()));
```

The lambda runs per context, so the second form gives every scope its own database. The seed,
the code under test and the assertions each talk to a different one, and the test fails with
everything empty rather than with anything that points at the cause.

## Getting data in

A rebuilt database is empty apart from two seeded duty-hour rules. Nothing is on call, so the
dashboard and Command Center will look bare until you add people.

**Staff** — Directory → **Download Template**, fill it in, then **Import CSV**. Real formatting
is fine: `(202) 555-0134`, `202-555-0134` and `+1 202 555 0134` are all normalized to E.164 on
import. Extensions and short fragments are still rejected, because a number that cannot be
dialled is worse in a directory than an empty field.

**Schedules** — create one against a department, then use **Generate shifts**. Shifts are
built in the hospital's local time zone (`Scheduling:TimeZone`, default `America/New_York`)
and every hour of the day gets a primary, including overnight.

**A second person's access** — have them sign in once. They then appear under
Admin → **Users & Permissions** → *Signed-in users*, flagged **No access**. Select them and
grant `Schedule.Read` + `Directory.Read` (the baseline from `docs/onboarding-standard.md`).
There is no list to pick from before their first sign-in; that is what the directory is for.

## What cannot work locally

Not bugs — missing credentials or deliberately disabled:

| Feature | Why |
|---------|-----|
| AD sync, calendar push, presence | Need the Graph client secret, which lives only in Key Vault |
| Teams notifications | Same credential |
| Twilio delivery callbacks | Twilio cannot reach `localhost`, so a send reports `queued` and never settles. Sending itself *does* work locally — see `docs/twilio-setup.md` §6 |
| Production data | Local uses its own SQLite file and never touches Azure SQL |

The sync services are switched off in the script rather than left to fail, so they do not bury
real output in authentication errors.

## Google sign-in locally

The Microsoft path is fully configured. Google may additionally need
`http://localhost:5173` registered as an **Authorized JavaScript origin** on the OAuth client
in Google Cloud Console — a Google-side change, unrelated to Azure. Without it the Google
button renders but the popup is blocked.

## Configuration files

| File | Committed? | Purpose |
|------|-----------|---------|
| `scripts/run-local-backend.sh` / `.ps1` | Yes | Real-SSO environment; **no secrets**, only public identifiers already in `deploy.yml` |
| `appsettings.Development.example.json` | Yes | Template for the gitignored real file |
| `appsettings.Development.json` | **No** | Your local overrides |
| `src/frontend/.env.local` | **No** | `VITE_DEV_AUTH=false` plus client ids; see `.env.local.example` |

Because `appsettings.Development.json` and `launchSettings.json` are both gitignored, the
reproducible setup lives in the scripts instead — environment variables override config files,
so the script wins regardless of what your local `appsettings.Development.json` says.
