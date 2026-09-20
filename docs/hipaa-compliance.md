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

| Data Element | PHI? | Stored | Encrypted | Retention |
|-------------|------|--------|-----------|-----------|
| Employee Name | Yes | Local DB + AD Sync | Column-encrypted | Duration of employment + 6yr |
| Department | No | Local DB | N/A | Indefinite |
| Role/Title | No | Local DB | N/A | Indefinite |
| Phone Number | Yes | Local DB + AD | Column-encrypted | Duration of employment + 6yr |
| Email | Yes | Local DB + AD | Column-encrypted | Duration of employment + 6yr |
| Office Location | No | Local DB | N/A | Indefinite |
| Schedule (who is on call) | No | Local DB | N/A | Indefinite (aggregate) |
| Schedule (assignments) | Yes | Local DB | Column-encrypted | 6 years |
| Swap Requests | Yes | Local DB | Column-encrypted | 6 years |
| Time-Off Records | No | Local DB | N/A | 3 years |
| Audit Logs | Yes | Azure Monitor | At-rest encrypted | 6 years |

## BAA Responsibilities

| Party | Responsibility |
|-------|---------------|
| **Organization** (Covered Entity) | Configure access correctly, train users, perform risk assessments, manage incidents |
| **Microsoft** (Business Associate) | Azure infrastructure security, physical security, network security, BAA compliance |
| **Application** (Our code) | Application-level access control, audit logging, PHI encryption, secure development lifecycle |
