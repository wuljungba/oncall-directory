---
name: production-readiness
description: Checklist used by infra-devops before any production deploy or slot swap.
---

# Production Readiness Checklist

- [ ] EF Core migrations applied and verified against target environment.
- [ ] Background sync intervals (`AdSyncIntervalMinutes`,
      `CalendarSyncIntervalMinutes`, `PresenceSyncIntervalMinutes`) set to
      real values in prod — not the dev `0` (disabled) setting.
- [ ] `AzureAd`/`GraphApi` config points at the correct production Entra
      tenant, not a dev/test tenant.
- [ ] `DevAuth:Enabled` is `false` in production `appsettings`.
- [ ] All secrets sourced from Key Vault / pipeline secrets, none in
      committed config files.
- [ ] Staging slot health check passes before swap (per
      `.github/workflows/deploy.yml`).
- [ ] `hipaa-compliance` sign-off obtained for any change touching PHI,
      auth, or the dispatch path in this release.
- [ ] Rollback plan confirmed (slot swap-back) before proceeding.

This checklist gates the production swap step specifically — staging
deploys can proceed once build/test/lint pass.
