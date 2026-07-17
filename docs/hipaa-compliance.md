# HIPAA Compliance Checklist

## Technical Safeguards

| Requirement | Implementation | Status |
|-------------|---------------|--------|
| Access Control | Entra ID RBAC + per-resource authorization | ✅ |
| Unique User IDs | Azure AD identity per user | ✅ |
| Automatic Logoff | 15-min inactivity timeout (configurable) | ✅ |
| Encryption in Transit | TLS 1.3 for all endpoints | ✅ |
| Encryption at Rest | Azure SQL TDE + column-level encryption for PHI fields | ✅ |
| Audit Controls | All PHI access logged with user, timestamp, action, resource | ✅ |
| Integrity Controls | Checksums on critical schedule data | ✅ |
| Person/Entity Auth | Multi-factor auth via Entra ID Conditional Access | ✅ |

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
