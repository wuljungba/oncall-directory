# Deploying the OnCall infrastructure

`main.bicep` is the complete definition of the app's Azure configuration. That word
"complete" is load-bearing — read the next section before running it.

## Why every setting must be in the template

ARM replaces `siteConfig.appSettings` **wholesale**. It does not merge. Any setting applied
by hand with `az webapp config appsettings set` and absent from this file is therefore
**deleted** the next time the template is deployed.

That had already happened in practice: `GraphApi__*`, `Authentication__Local__SigningKey`,
`Authentication__SuperAdmins__*` and `AzureAd__Audience` were all hand-set and none were in
the template. A redeploy would have produced an app that could not sync from Entra, could
not validate a local token, had no super administrator, and rejected every sign-in — with
no error at deploy time, because from ARM's point of view it succeeded.

**If you add a setting by hand, add it here too, or expect to lose it.**

## Where each value comes from

| Kind | Mechanism | Examples |
|------|-----------|----------|
| Secrets | Key Vault reference resolved at runtime | `GraphApi__ClientSecret`, `Authentication__Local__SigningKey`, `ConnectionStrings__DefaultConnection`, `Dispatch__Twilio__AuthToken` |
| Public identifiers | Bicep parameters | `graphClientId`, `googleClientId`, `entraClientId` |
| Operational tuning | Bicep parameters with defaults | sync intervals, `schedulingTimeZone`, HIPAA retention |
| Derived | Computed in the template | `AzureAd__Audience`, the Twilio status-callback URL (per slot) |

No secret value appears in this template, in a parameter file, or in the deployment
history. The app's managed identity holds **Key Vault Secrets User**, granted here.

### Secrets to create before deploying

```bash
read -rs VALUE && az keyvault secret set --vault-name <vault> --name SqlConnectionString --value "$VALUE" && unset VALUE
```

Repeat for each: `SqlConnectionString`, `GraphApiClientSecret`, `LocalJwtSigningKey`,
`TwilioAuthToken`.

`LocalJwtSigningKey` must be at least 32 characters — the app refuses to start in
production otherwise.

## Parameters worth setting deliberately

| Parameter | Default | Why it matters |
|-----------|---------|----------------|
| `superAdminEmails` / `superAdminObjectIds` | `[]` | **Empty means nobody can hold `Admin.Full`** — the environment deploys with no administrator |
| `schedulingTimeZone` | `America/New_York` | Decides what "7am" means when a rotation is generated; wrong value puts every shift at the wrong time of day |
| `graphClientId` | `''` | Blank disables AD sync, calendar push and presence |
| `googleClientId` | `''` | Blank means Google tokens fail audience validation, so Google sign-in breaks |
| `twilioEnabled` | `false` | Leave false until the Key Vault secret and A2P registration exist — see `docs/twilio-setup.md` |
| `adSyncIntervalMinutes` | `15` | `0` disables the sync |

## Slot-sticky settings

Two settings are pinned to their slot via `slotConfigNames`, so a swap does **not** move
them:

- `GraphApi__ClientSecret` — production holds the current rotated secret; staging may hold
  an older one, and a swap must not push a stale credential into production.
- `WEBSITE_HTTPLOGGING_RETENTION_DAYS` — platform-managed and prone to drifting back.

Stickiness is configured by name only (`properties.appSettingNames`), so no secret value is
read or written to mark one.

## Deploying

```bash
az deployment group create --resource-group rg-oncall-production --template-file infrastructure/bicep/main.bicep --parameters environmentName=production entraTenantId=<tenant> entraClientId=<client> entraDomain=<domain> superAdminEmails='["admin@hospital.org"]' schedulingTimeZone=America/New_York
```

Afterwards, confirm nothing was lost:

```bash
az webapp config appsettings list --name app-oncall-production --resource-group rg-oncall-production --query "sort_by([].name, &@)" -o tsv
```

Compare that against the same command for the `staging` slot. Anything present on one and
absent from the other will move during a swap unless it is slot-sticky.
