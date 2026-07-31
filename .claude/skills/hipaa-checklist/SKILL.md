---
name: hipaa-checklist
description: Checklist used by hipaa-compliance to review any change touching PHI, auth, audit logging, or the code-call dispatch path.
---

# HIPAA Compliance Checklist

For each task in scope, check and record pass/fail with evidence:

- [ ] **PHI fields encrypted** — every PHI-bearing column uses EF Core
      Always Encrypted (or equivalent); no new PHI field added without it.
- [ ] **Audit coverage** — `HipaaAuditMiddleware` logs every endpoint that
      reads or writes PHI; no route silently bypasses it.
- [ ] **Audit durability** — `AuditBackgroundService` flush is confirmed
      working, not failing silently; retention matches
      `Hipaa:AuditLogRetentionDays` (default 2190 days).
- [ ] **Session policy** — `Hipaa:SessionTimeoutMinutes` enforced
      server-side, not just in the frontend.
- [ ] **Transport security** — TLS 1.2+ enforced on all endpoints handling
      PHI or auth tokens.
- [ ] **Access scoping** — tenant/role checks enforced at the controller
      level, not only hidden in the UI.
- [ ] **Dispatch reliability** — code-call/escalation/phone-tree paths fail
      loudly (logged + alerted), never silently, on any error.
- [ ] **No PHI in logs/specs** — confirm no PHI value ever appears in spec
      files, chat output, or non-audit logs.

Any unchecked item blocks sign-off until resolved or explicitly accepted by
the user with a stated reason.
