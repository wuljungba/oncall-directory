# Always-On Guardrails (apply to every agent, every task)

- Never print PHI values in chat, specs, logs, or commit messages —
  reference records by non-PHI identifiers (e.g., record ID) only.
- Never disable or weaken JWT validation, encryption, audit middleware, or
  session timeout to "make something work" or "just for testing" — use the
  existing `DevAuth` dev-mode pattern instead.
- Never commit secrets, connection strings, or client secrets — config keys
  only, values from Key Vault/env.
- Treat the code-call/escalation/phone-tree path as safety-critical: no
  silent failures, no swallowed exceptions, no "best effort" delivery
  without an alert on failure.
- Production and staging are separate; nothing reaches production without
  passing staging health checks and, where applicable, explicit user
  sign-off.
- When uncertain whether something is PHI, tenant-sensitive, or
  safety-critical, treat it as if it is and ask, rather than assuming it
  isn't.
