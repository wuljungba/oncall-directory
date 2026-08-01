# Baseline: Frontend Auth Wiring Discovery (React/Vite)

**Date**: 2026-07-31
**Scope**: Frontend Specialist (read-only audit). Covers the dev-auth short-circuit,
MSAL/Google provider wiring, env-file precedence, and how `activeTenantId` / auth
state flow through the SPA.
**Cross-ref**: `baseline-auth.md` covers the backend JWT pipeline, DevAuth handler,
and Graph API. This file is the frontend counterpart; read both together.

---

## 1. How the app decides auth mode

Single gate everywhere: `DEV_AUTH = import.meta.env.VITE_DEV_AUTH === 'true'`.

| File | Line | Effect |
|------|------|--------|
| `src/frontend/.env` | 15 | `VITE_DEV_AUTH=true` (the current local default) |
| `src/frontend/src/main.tsx` | 22, 56-58 | if DEV_AUTH → `renderApp()` with NO MSAL, NO MsalProvider, NO GoogleOAuthProvider |
| `src/frontend/src/hooks/useAuth.ts` | 6, 44-51, 83-94, 121-132, 168-176, 186 | dev fake user pre-seeded; `signIn`/`refreshToken`/`isAuthenticated` short-circuited |
| `src/frontend/src/services/api.ts` | 33, 45 | `getAuthToken()` returns `sessionStorage.accessToken` (null in dev) without touching a provider |
| `src/frontend/src/components/OnboardingWizard.tsx` | 27 | `canUseMicrosoft = authProvider === 'microsoft' \|\| VITE_DEV_AUTH === 'true'` |

Fresh-clone trap: `.env` is gitignored, so a fresh clone has no env file. `VITE_DEV_AUTH`
is then `undefined` → DEV_AUTH=false → the app takes the real-auth path with a
placeholder client ID (see §4). Dev mode only exists because the gitignored `.env`
happens to be present on disk.

## 2. What happens when the user clicks "Sign in" today (dev mode)

Trace (all DEV_AUTH=true):

1. `LoginPage.tsx:108` → `handleSsoSignIn('microsoft')` (`LoginPage.tsx:30-34`) → `signIn('microsoft')`.
2. `useAuth.signIn` (`useAuth.ts:121-132`): DEV_AUTH branch — does NOT touch MSAL,
   Google, or the local endpoint. It fabricates the user `{id:'dev', name:'dev@local',
   email:'dev@local', provider:'microsoft'}` (line 124), sets `authProvider='microsoft'`
   (line 125), then calls `/api/auth/me` for permissions (line 127-129).
3. In practice the login page is rarely even seen: `isAuthenticated` is `true` from
   mount (`useAuth.ts:186`, initial state line 44-51), so `LoginPage.tsx:26-28`
   immediately `navigate('/dashboard')`, and `ProtectedRoute` (`App.tsx:19-24`) lets
   the user straight through. The session is dev@local, presented as "Microsoft".

Because every provider is short-circuited, clicking "Sign in with Google" or the
Local Account tab (even with bogus credentials) also silently produces dev@local.

Frontend/backend divergence trap: dev auth works only if the backend also runs with
`DevAuth:Enabled: true`. If the frontend is dev-auth but the backend is real-auth,
`/api/auth/me` and all subsequent calls 401 (no bearer token is ever attached;
`api.ts:45` returns null). The two flags must be toggled together.

## 3. Env-file inventory and precedence

Files present in `src/frontend/`: `.env` (676 B) and `.env.example` (298 B). No
`.env.local`, `.env.development`, `.env.production`.

Git state (root `.gitignore` lines 4-5): `.env` and `.env.local` ignored; `.env.example`
is tracked. `git ls-files` confirms only `.env.example` is committed.

Gaps:
- `.gitignore` does NOT cover `.env.development.local`, `.env.production.local`, or
  any `*.local` env file. Vite convention is to ignore `*.local`; following it today
  would let real client IDs get committed.
- `.env.example` omits `VITE_GOOGLE_CLIENT_ID` (present in `.env`) and has the
  `VITE_DEV_AUTH=true` line commented out (`.env` has it active).
- `microsoftAuthProvider.ts:10` fallback is `'your-api-client-id'`, while `.env`
  uses `'your-spa-client-id'` — two different placeholder strings; harmless but
  confusing, and it means the exact placeholder depends on whether `.env` exists.

Vite load order (later wins): `.env` < `.env.local` < `.env.[mode]` < `.env.[mode].local`.
Only `VITE_`-prefixed vars reach `import.meta.env`.

## 4. Behavior with `VITE_DEV_AUTH=false` + placeholder client IDs

Verified against installed `@azure/msal-browser` 3.30.0 and `@react-oauth/google` 0.13.5:

- MSAL: `PublicClientApplication.initialize()` (`StandardController.mjs:141-173`) does
  NOT validate client-ID format and makes NO network request; it resolves. So
  `main.tsx:64-69` renders WITH MsalProvider; the `.catch` fallback (render without
  MSAL) does not fire. No hang, no crash at startup.
- Clicking "Sign in with Microsoft": `loginPopup` (`microsoftAuthProvider.ts:92`)
  navigates the popup to the authorize endpoint with `client_id=your-spa-client-id`;
  AAD rejects with AADSTS700016 (app not found) inside the popup; the rejection is
  caught (`microsoftAuthProvider.ts:117-120`), `signIn()` returns null, and
  `useAuth` (line 141) leaves the user signed out. Net: popup shows an AAD error;
  the main app keeps working (logged out).
- Google: `GOOGLE_ENABLED` is true (`main.tsx:27`), so `GoogleOAuthProvider` mounts
  and loads the GIS script with the placeholder `your-google-client-id.apps.googleusercontent.com`.
  `@react-oauth/google` does not validate clientId at mount (`dist/index.js:88`), so
  no crash — but an unnecessary external script loads, and the Google button would
  fail if clicked. Harmless for a Microsoft-only user, but sloppy.

## 5. Dev-mode UI indicators

- NONE. No banner or badge anywhere. `Layout.tsx:83-90` shows the user email in the
  sidebar ("dev@local" in dev) — the only signal, and easy to miss.
- `Layout.tsx:119-129` shows a SignalR "Live/Offline" dot. In dev, SignalR never
  connects (`useSignalR.tsx:31-33` gets a null token and bails), so dev always shows
  "Offline" — itself misleading.
- `OnboardingWizard.tsx:170-174` prints "Signed in with **Microsoft**" because the
  dev fake user's provider is 'microsoft' (see §6).

## 6. Fake-user provider masking

Confirmed: `useAuth.ts:46` (initial) and `:124` (sign-in) set `provider: 'microsoft'`
on the dev@local fake user; `:49`/`:125` set `authProvider='microsoft'`. Consequences:

- Everything keyed off `authProvider === 'microsoft'` behaves/renders as Microsoft:
  the login button label, `OnboardingWizard.tsx:27` (enables the "Microsoft 365 /
  Active Directory" sync option in dev), and the "Signed in with Microsoft" header.
- `handleChooseMicrosoft` (`OnboardingWizard.tsx:53-71`) calls `/api/integrations/sync/ad`;
  with no backend Graph creds it fails but is caught (lines 63-70) and still toasts
  "Configured" — a dev user can wander through an AD-sync flow that isn't real.
- This is the masking behind the reported symptom: the session looks Microsoft-branded
  but is dev@local with no real token.

## 7. Recommended minimal env strategy

1. `.env` (gitignored) stays the day-to-day dev-auth default: `VITE_DEV_AUTH=true` +
   placeholders. Leave it alone.
2. `.env.local` (gitignored; currently absent) is the file for REAL Entra testing:
   ```
   VITE_DEV_AUTH=false
   VITE_AZURE_CLIENT_ID=<real SPA client id>
   VITE_GOOGLE_CLIENT_ID=
   ```
   `.env.local` overrides `.env`, so no edit to `.env` is needed. To return to dev
   auth, delete `.env.local` (or set `VITE_DEV_AUTH=true` in it).
3. `.env.example` should be updated to include `VITE_GOOGLE_CLIENT_ID` and document
   the two-file strategy.
4. `.gitignore` should add `.env.*.local` (currently only bare `.env`/`.env.local`).
5. Backend must also run with `DevAuth:Enabled: false` for real Entra (the two flags
   must match, per §2).
6. Note `microsoftAuthProvider.ts:10` fallback placeholder (`your-api-client-id`)
   should be reconciled with `.env`/`.env.example` (`your-spa-client-id`).

## 8. Open questions

- Is the intended long-term developer workflow a committed `.env.development`
  (real-ish IDs, committed) vs `.env.development.local` (dev-auth)? The current
  repo has neither; the answer determines the exact two-file story.
- MSAL config values and app registration (redirect URI, SPA platform, `api://`
  scope) belong to `entra-identity`; the frontend audit flags but does not resolve
  them.

## 9. Not yet reviewed (adjacent, out of scope)

- Backend JWT validation / `DevAuth:Enabled` handler (see `baseline-auth.md`).
- MSAL app registration, redirect URI config, token validation (`entra-identity`).
- `services/signalr.ts` connection lifecycle details (token source noted in §5).
