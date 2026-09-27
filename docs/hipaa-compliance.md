# HIPAA Compliance Checklist

## Technical Safeguards

> **Status key:** ✅ implemented · ⚠️ partially implemented · ❌ not implemented.
> Every row below was re-verified against the code and the live tenant on **2026-09-20**.
> Four rows previously read ✅ and were wrong; they are corrected here. Do not restore a ✅
> to any row without checking the thing it claims.

| Requirement | Implementation | Status |
|-------------|---------------|--------|
| Access Control | Entra ID RBAC + per-resource authorization | ✅ |
| Unique User IDs | Azure AD identity per user | ✅ |
| Automatic Logoff | Inactivity timeout, configurable via `Hipaa:SessionTimeoutMinutes` | ✅ |
| Encryption in Transit | TLS **1.2** floor enforced (`minTlsVersion: '1.2'`, `infrastructure/bicep/main.bicep`); 1.3 is negotiated where the client supports it, but is not the enforced minimum | ✅ |
| Encryption at Rest | Azure SQL TDE, which encrypts database files. **There is no column-level or application-level PHI encryption** — no Always Encrypted, no field encryption in code. Anything holding a database connection reads PHI in plaintext | ⚠️ |
| Audit Controls | All PHI access logged with user, timestamp, action, resource (`HipaaAuditMiddleware`, flushed by `AuditBackgroundService`) | ✅ |
| Integrity Controls | **Not implemented.** No checksums exist on schedule data; the only HMAC in the codebase is JWT signing in `LocalJwtService` | ❌ |
| Person/Entity Auth | **Not configured.** The tenant holds no licensed SKUs (Entra ID Free), where Conditional Access is unavailable, and it has **0 Conditional Access policies**. Any MFA would have to come from security defaults, which could not be read and remains unverified | ❌ |

## Contingency Plan — §164.308(a)(7)

Backups, recovery, retention and the restore runbook: **[backup-and-recovery.md](backup-and-recovery.md)**.

| Requirement | Implementation | Status |
|---|---|---|
| Data Backup Plan | Azure SQL: 35-day point-in-time restore, geo-redundant backup storage, and long-term backups kept weekly 12w / monthly 84m / yearly 7y. Audit rows and code-call history additionally exported to blob storage as NDJSON | ✅ |
| Disaster Recovery Plan | Runbook exists in backup-and-recovery.md, but **no restore has ever succeeded** on this server — three attempts on 2026-09-24 produced nothing. No failover group or geo-replica either | ❌ |
| Emergency Mode Operation | Not documented | ❌ |
| Testing and Revision | A drill was **attempted** 2026-09-24 and **failed** — all three restore methods produced no database, silently. Logged in backup-and-recovery.md. This row must not read ✅ until a restore has actually completed | ❌ |
| Applications and Data Criticality Analysis | Not documented | ❌ |

Retention is seven years (`Hipaa:AuditLogRetentionDays` = 2555), and it is enforced: the
application refuses to start outside Development if configured below that floor, and the audit
archive refuses to delete anything while it is.

> **Do not represent §164.308(a)(7) as satisfied.** Backups are configured and verified as
> configured; recovery from them is unproven. An earlier revision of this table said a drill had
> been performed, which was true only in the sense that one was attempted — it failed. Retention
> policy without a working restore is a filing system, not a contingency plan.

## Physical Safeguards (Delegated to Azure)

- Azure SOC 1/2/3 Type II certified
- BAA signed with Microsoft
- Data stored in US regions only (configurable)
- Geo-redundant backup with encryption

## Administrative Safeguards

- Automatic session timeout
- Role-based access reviews (quarterly recommended)
- Breach notification within 60 days (HIPAA requirement)
- Minimum necessary access principle enforced via Entra ID groups

## PHI Data Inventory

> **Nothing in this system is column-encrypted.** Five rows of this table used to say
> "Column-encrypted" for the most sensitive fields here — name, phone, email, schedule
> assignments and swap requests. That was never implemented and directly contradicted the
> technical-safeguards table above. Corrected 2026-09-27. At-rest protection everywhere is
> Azure SQL **Transparent Data Encryption**, which encrypts database *files* — anything
> holding a database connection reads every field below in plaintext.

| Data Element | PHI? | Stored | Encrypted | Retention |
|-------------|------|--------|-----------|-----------|
| Employee Name | Yes | Local DB + AD Sync | TDE only (file-level) | Duration of employment + 7yr |
| Department | No | Local DB | TDE only | Indefinite |
| Role/Title | No | Local DB | TDE only | Indefinite |
| Phone Number | Yes | Local DB + AD | TDE only (file-level) | Duration of employment + 7yr |
| Email | Yes | Local DB + AD | TDE only (file-level) | Duration of employment + 7yr |
| Office Location | No | Local DB | TDE only | Indefinite |
| Schedule (who is on call) | No | Local DB | TDE only | Indefinite (aggregate) |
| Schedule (assignments) | Yes | Local DB | TDE only (file-level) | 7 years |
| Swap Requests | Yes | Local DB | TDE only (file-level) | 7 years |
| Time-Off Records | No | Local DB | TDE only | 3 years |
| Audit Logs | Yes | **Azure SQL `AuditLogs` table**, archived to blob storage after 90 days | TDE / Azure Storage SSE | 7 years (`Hipaa:AuditLogRetentionDays` = 2555, enforced at startup) |
| Code-call incidents | Yes | Local DB + `incident-archive` blob | TDE / Azure Storage SSE | 7 years; cannot be deleted through the app |
| Debrief notes | Yes — free-text clinical narrative | Local DB + `incident-archive` blob | TDE / Azure Storage SSE | 7 years, append-only |

Two corrections to the "Stored" column beyond encryption: audit logs live in the SQL
`AuditLogs` table and then in blob storage, **not** in Azure Monitor as this table previously
claimed — Azure Monitor here retains 30 days, which would not satisfy any of these rows. And
any exported copy of this data (a per-tenant backup, a bacpac, a blob archive) carries every
field above in plaintext, so it must be encrypted before it leaves Azure — see
[backup-and-recovery.md](backup-and-recovery.md).

## BAA Responsibilities

| Party | Responsibility |
|-------|---------------|
| **Organization** (Covered Entity) | Configure access correctly, train users, perform risk assessments, manage incidents |
| **Microsoft** (Business Associate) | Azure infrastructure security, physical security, network security, BAA compliance |
| **Application** (Our code) | Application-level access control, audit logging, PHI encryption, secure development lifecycle |
