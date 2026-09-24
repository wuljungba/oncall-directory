# Backup, recovery and retention

What protects tenant data, what it does not protect against, and how to get data back.

Every figure below was read from the live subscription, not from a template. Where the
infrastructure and the documentation disagreed, the infrastructure won and the documentation
has been corrected.

---

## What is in place

> **Restore is unproven.** The policies below are verified as *configured*, but no restore has
> ever completed on this server — three attempts on 2026-09-24 produced nothing, silently. See
> [Runbook](#runbook). Configuration is not recovery.


| Control | Setting | What it buys |
|---|---|---|
| Point-in-time restore | **35 days** | Restore to any second in the last 35 days |
| Long-term backups | **weekly 12w · monthly 84m · yearly 7y** | A restore point per month across the full seven-year retention |
| Backup storage | **Geo-redundant** | Survives loss of the primary region |
| Blob versioning | **on** | An overwritten archive blob can be rolled back |
| Blob / container soft delete | **90 days** | A deleted archive file can be recovered |
| Delete locks | SQL server, Key Vault, storage account | `az group delete` cannot take the data with it |
| Key Vault soft delete | **7 days**, purge protection **on** | A deleted vault is recoverable for 7 days and cannot be purged early. 7 is Azure's minimum and is **immutable once a vault is created** — raising it needs a new vault |
| Audit archive | blob NDJSON, `audit-archive` | Audit rows outlive the database table |
| Incident archive | blob NDJSON, `incident-archive` | Code-call history exists outside SQL, **and stays in it** |

Retention is **2555 days (seven years)**. `RetentionPolicy` reads that value and the app
**refuses to start** outside Development if it is configured below the floor. It used to be a
number in `appsettings.json` that no code read at all.

### Why the incident archive copies rather than moves

`AuditArchiveService` deletes rows from SQL once they are safely in blob storage, because audit
rows are high-volume and rarely read. `IncidentArchiveService` deliberately does not. The
command center history is the record someone opens during a review, and evicting it to satisfy
a retention policy would make the seven-year record unreadable in the product required to keep
it. Volume does not justify it either — a hospital produces a handful of code calls a day.

It writes one file per calendar month and rewrites only the last two, so a pass costs the same
whether the system is a week old or seven years old.

---

## What this does **not** protect against

Be clear about these; the table above can otherwise read as more reassuring than it is.

1. **Staging and production share one database.** Both slots resolve
   `ConnectionStrings__DefaultConnection` to the same Key Vault secret. A staging deploy runs
   its schema DDL against production data the moment the slot starts, and the documented
   rollback — swapping the slots back — restores **code only**. It cannot undo a schema or data
   change. This is the largest remaining data-loss vector and the fix is a separate
   `sqldb-oncall-staging`.

2. **Restores are all-or-nothing across tenants.** One database with a `TenantId` column means
   recovering one customer's data rolls every other customer back to the same moment. There is
   no per-tenant restore.

3. **A logical loss is backed up faithfully.** Backups protect against hardware, region and
   accidental deletion. They do not protect against a bad migration or an unforeseen cascade,
   because the damage is copied into every subsequent backup. Only noticing it inside the
   35-day window does. The blob archives are the mitigation: a month file, once closed, is
   never rewritten.

4. **No alerting.** There are no metric or scheduled-query alert rules in the resource group,
   so nothing detects a loss inside the window that makes it recoverable. Detection is
   currently a person noticing.

5. **No failover group or geo-replica.** Backup storage is geo-redundant, so a geo-restore is
   possible, but there is no standby to fail over to. Regional recovery means a restore, at the
   RTO below.

---

## Runbook

### Restore to a point in time (last 35 days)

Restores **never overwrite** the live database — they create a new one. That is what makes this
safe to rehearse and safe to do under pressure.

> ## ⚠️ RESTORE IS UNPROVEN — READ THIS BEFORE YOU NEED IT
>
> **No method tried on 2026-09-24 produced a restored database.**
> Three attempts, none of which created anything:
>
> 1. `az sql db restore` — accepted, returned no error, **issued no write to Azure**, then
>    blocked indefinitely waiting for a database it never requested.
> 2. The same with `--no-wait` — returned successfully, equally inert.
> 3. The ARM REST `PUT` below — returned `{"operation":"CreateRestoreRequest"}`, which looks
>    like acceptance, but **no database ever appeared and no write operation was ever recorded
>    in the activity log**.
>
> Ruled out: the subscription is `Enabled`, and other ARM writes against this resource group
> on the same day succeeded (long-term retention policy, point-in-time retention, resource
> locks, Key Vault purge protection). So writes work in general; restores specifically do not
> start, and Azure reports no error explaining why.
>
> **What this means.** The backup *configuration* is verified — the policies below were all
> read back from Azure. Whether a backup can actually be restored is **not** verified, and
> three attempts say it is not straightforward. Treat recovery as an open risk until someone
> completes a restore, ideally through the Azure Portal, which surfaces errors the API is
> swallowing here.
>
> **This is the single most important open item in this document.** Backups whose restore path
> has never worked are not yet backups.

```bash
# 1. Confirm the window
az sql db show -g rg-oncall-prod -s sql-oncall-prod -n sqldb-oncall-prod \
  --query "{earliest:earliestRestoreDate}" -o json

# 2. Restore beside production, never over it
SUB=d516eda6-511d-4ffd-8547-205d62548b39
SRC="/subscriptions/$SUB/resourceGroups/rg-oncall-prod/providers/Microsoft.Sql/servers/sql-oncall-prod/databases/sqldb-oncall-prod"

cat > restore.json <<JSON
{"location":"westus3",
 "sku":{"name":"S0","tier":"Standard"},
 "properties":{"createMode":"PointInTimeRestore",
               "sourceDatabaseId":"$SRC",
               "restorePointInTime":"2026-09-24T18:00:00Z"}}
JSON

az rest --method PUT --body @restore.json \
  --url "https://management.azure.com$SRC/../sqldb-oncall-recovered?api-version=2023-08-01-preview"

# 3. Poll until it appears. A restore takes tens of minutes even for a small database.
az sql db list -g rg-oncall-prod -s sql-oncall-prod --query "[].{name:name,status:status}" -o table

# 3. Point the app at it only after checking the data is what you expect,
#    by updating ConnectionStrings__DefaultConnection in Key Vault.
```

### Restore from a long-term backup (older than 35 days)

```bash
# List what exists — LTR backups are addressed by their own resource id
az sql db ltr-backup list -l westus3 -s sql-oncall-prod -d sqldb-oncall-prod -o table

az sql db ltr-backup restore --dest-database sqldb-oncall-recovered \
  --dest-server sql-oncall-prod --dest-resource-group rg-oncall-prod \
  --backup-id <id from the list above>
```

### Read the archives without a database

Both containers hold NDJSON — one JSON object per line, readable with `jq` or any text tool,
with no dependency on the application:

```bash
az storage blob download --account-name stprodnaxpflhimpaii \
  -c incident-archive -n 2026/09/incidents-2026-09.jsonl -f ./incidents.jsonl
jq -r '[.Id, .Code, .StartedAt, .InitiatedByName, .InitiatedByEmail] | @tsv' incidents.jsonl
```

Audit rows are in `audit-archive`, foldered the same way.

### If a resource will not delete

That is the lock doing its job. Remove it deliberately, and put it back:

```bash
az lock delete -g rg-oncall-prod -n protect-sql-oncall-prod \
  --resource-type Microsoft.Sql/servers --resource-name sql-oncall-prod
```

---

## Recovery objectives

- **RPO — point-in-time:** effectively seconds. Azure SQL takes continuous log backups; any
  second within the 35-day window is addressable.
- **RPO — long-term:** one month, from the monthly LTR backups, for anything older than 35 days.
- **RTO: unknown.** No restore has ever completed here — see the drill log. Every figure in
  this section is a property of the configuration, not a demonstrated recovery.

### Drill log

A backup nobody has restored is a hypothesis. Record every drill here.

| Date | Type | Result | Notes |
|---|---|---|---|
| 2026-09-24 | PITR to a new DB via `az sql db restore` | **Failed to submit** | CLI accepted the command, issued no write, blocked indefinitely. Twice, including `--no-wait`. See the warning above |
| 2026-09-24 | PITR to a new DB via ARM REST | **No database produced** | Returned `CreateRestoreRequest`, but nothing was created and no write appears in the activity log, 40+ min later |

What the drill established, and what it did not:

- **Established:** the documented restore path does not work, by any of three routes, and
  fails *silently* — no error, no database. Had this been attempted for the first time during
  an incident, the failure would have been discovered at the worst possible moment. That alone
  justified running the drill.
- **Not established:** that any backup here can be restored at all. Next step is a restore
  from the **Azure Portal**, which reports errors the CLI and REST API are swallowing. Until
  that succeeds, the recovery story is unproven — and no amount of retention policy
  substitutes for it.

**RTO is therefore unknown, not "tens of minutes".** Do not quote one until a restore has
completed.

**Re-run the drill after any tier change, any significant growth, and at least annually.**
