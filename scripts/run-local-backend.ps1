# Run the OnCall API locally with REAL Microsoft/Google sign-in.
#
# PowerShell twin of run-local-backend.sh. See that file and docs/local-development.md for
# the reasoning; the short version is that the default development experience uses DevAuth,
# which auto-authenticates every request as a fake all-roles admin and so bypasses the whole
# auth stack.
#
# NO SECRETS HERE. Every value is a public identifier already committed in
# .github/workflows/deploy.yml.
#
# Usage:  .\scripts\run-local-backend.ps1

$ErrorActionPreference = 'Stop'

Set-Location (Join-Path $PSScriptRoot '..\src\backend\OnCallApi')

# ── Real authentication ────────────────────────────────────────────────────────
$env:DevAuth__Enabled = 'false'

$env:AzureAd__TenantId = '24b3700e-7053-4498-a4e6-b8ebf85dc38c'
$env:AzureAd__ClientId = '96955ba3-c70c-4205-8637-a4b34301480a'
$env:AzureAd__Domain   = 'yisadivinyahoo.onmicrosoft.com'
$env:AzureAd__Audience = 'api://96955ba3-c70c-4205-8637-a4b34301480a'

$env:Authentication__Google__ClientId = '445006464104-pcq13k9lkmcol1k5hqktu8arcrv49c5n.apps.googleusercontent.com'

# Without this you sign in and then land on the "access pending" panel: a real Entra token
# carries no app roles, so super-admin comes from configuration alone.
$env:Authentication__SuperAdmins__Emails__0 = 'yisadivin@yahoo.fr'

# ── Background sync: off (Graph client secret lives only in Key Vault) ─────────
$env:Sync__AdSyncIntervalMinutes       = '0'
$env:Sync__CalendarSyncIntervalMinutes = '0'
$env:Sync__PresenceSyncIntervalMinutes = '0'

Write-Host 'Starting OnCall API on http://localhost:5000 with real Entra sign-in (DevAuth off).'
Write-Host "Frontend: run 'npm run dev' in src/frontend (it proxies /api and /hubs here)."
Write-Host ''

dotnet run --urls 'http://localhost:5000'
