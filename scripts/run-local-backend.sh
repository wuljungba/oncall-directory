#!/usr/bin/env bash
#
# Run the OnCall API locally with REAL Microsoft/Google sign-in.
#
# The default development experience uses DevAuth, which auto-authenticates every request as
# a fake all-roles admin. That is fine for UI work but bypasses the entire auth stack, so
# nothing about permissions, tenant scoping or the sign-in directory can be tested. This
# script turns DevAuth off and supplies the real Entra configuration instead.
#
# It lives in the repo on purpose: appsettings.Development.json and launchSettings.json are
# both gitignored, so neither can carry a reproducible setup.
#
# NO SECRETS HERE. Every value below is a public identifier already committed in
# .github/workflows/deploy.yml. The Graph client secret is deliberately absent — see the
# note on sync services below.
#
# Usage:  ./scripts/run-local-backend.sh
# Docs:   docs/local-development.md

set -euo pipefail

cd "$(dirname "$0")/../src/backend/OnCallApi"

# ── Real authentication ────────────────────────────────────────────────────────
# The switch that makes JwtValidationMiddleware and the real JWT pipeline run.
export DevAuth__Enabled=false

# Tenant "yisadivinyahoo.onmicrosoft.com" and the "OnCall API" app registration.
# http://localhost:5173 is already a registered SPA redirect URI on this app.
export AzureAd__TenantId=e9577a81-c4d6-4124-984f-78f3c0efcaf4
export AzureAd__ClientId=6569f3cb-47a1-4826-9f35-16e7d4bf3a52
export AzureAd__Domain=yisadivinyahoo.onmicrosoft.com
export AzureAd__Audience=api://6569f3cb-47a1-4826-9f35-16e7d4bf3a52

export Authentication__Google__ClientId=445006464104-pcq13k9lkmcol1k5hqktu8arcrv49c5n.apps.googleusercontent.com

# Without this you sign in successfully and then land on the "access pending" panel: a real
# Entra token carries no app roles, so super-admin comes from configuration alone.
export Authentication__SuperAdmins__Emails__0=yisadivin@yahoo.fr

# ── Background sync: off ───────────────────────────────────────────────────────
# AD sync, calendar push and presence all need the Graph client secret, which lives only in
# Key Vault. Left enabled they would loop on authentication failures and bury real log output.
export Sync__AdSyncIntervalMinutes=0
export Sync__CalendarSyncIntervalMinutes=0
export Sync__PresenceSyncIntervalMinutes=0

# ── Twilio SMS: opt-in, credentials from YOUR environment ─────────────────────
# Deliberately NOT hardcoded. The Auth Token is a secret and must never be committed, so
# this only passes through what you export yourself:
#
#   export TWILIO_ACCOUNT_SID='AC...'
#   export TWILIO_AUTH_TOKEN='...'          # secret — keep it out of shell history
#   export TWILIO_FROM_NUMBER='+1...'       # must be E.164
#   ./scripts/run-local-backend.sh
#
# Without TWILIO_AUTH_TOKEN the channel stays disabled and the app behaves exactly as it
# does in production today. Startup refuses to boot if the channel is enabled with a
# malformed or missing sender, so a half-configured setup fails loudly rather than silently.
if [ -n "${TWILIO_AUTH_TOKEN:-}" ]; then
  export Dispatch__Twilio__Enabled=true
  export Dispatch__Twilio__AccountSid="${TWILIO_ACCOUNT_SID:-}"
  export Dispatch__Twilio__AuthToken="${TWILIO_AUTH_TOKEN}"
  export Dispatch__Twilio__FromNumber="${TWILIO_FROM_NUMBER:-}"
  export Dispatch__Twilio__MessagingServiceSid="${TWILIO_MESSAGING_SERVICE_SID:-}"

  # Twilio must reach this URL from the public internet to report delivery. localhost is
  # unreachable, so leave it unset unless you are running a tunnel (ngrok et al) and set
  # TWILIO_STATUS_CALLBACK_URL to the tunnel's /api/public/twilio/status address. Without
  # it a send is reported as "queued" and delivery failures cannot be detected.
  export Dispatch__Twilio__StatusCallbackUrl="${TWILIO_STATUS_CALLBACK_URL:-}"

  echo "Twilio SMS: ENABLED (sender ${TWILIO_FROM_NUMBER:-${TWILIO_MESSAGING_SERVICE_SID:-unset}})"
  if [ -z "${TWILIO_STATUS_CALLBACK_URL:-}" ]; then
    echo "  note: no status callback URL — sends will report 'queued', delivery is not confirmed."
  fi
else
  # Deliberately not "disabled": this branch only knows that no credentials came from the
  # environment. appsettings.Development.json can still enable the channel, and claiming
  # otherwise here sends you hunting for a problem that does not exist. The API's own
  # startup output is the authority.
  echo "Twilio SMS: no credentials in the environment."
  echo "  (appsettings.Development.json may still enable it — see docs/twilio-setup.md.)"
fi

echo "Starting OnCall API on http://localhost:5000 with real Entra sign-in (DevAuth off)."
echo "Frontend: run 'npm run dev' in src/frontend (it proxies /api and /hubs here)."
echo

exec dotnet run --urls "http://localhost:5000"
