---
name: entra-integration-audit
description: Checklist for auditing the current Microsoft Entra ID / Graph API integration before proposing auth changes. Used by entra-identity.
---

# Entra Integration Audit

Walk through and record the answer to each item in
`.claude/specs/baseline-auth.md`:

1. **Token validation path** — which middleware validates incoming JWTs,
   what issuer/audience/signature checks run, where is this configured
   (`AzureAd` section in `appsettings.json`)?
2. **Dev bypass** — is `DevAuth:Enabled` / `VITE_DEV_AUTH` on in any
   environment it shouldn't be? Confirm production config explicitly
   disables it.
3. **Graph API auth** — confirm `GraphApiService` uses app-only
   `ClientSecretCredential`, not delegated user tokens. List the actual
   Graph scopes/permissions granted vs. used in code — flag any
   over-provisioned scope.
4. **Multi-provider routing** — how does `authFactory` decide
   Microsoft/Google/Local? Is there any path where a user could end up
   authenticated by the wrong provider for their tenant?
5. **Tenant claims** — does `TenantClaimsMiddleware` run before every
   tenant-scoped controller action, with no bypassable route?
6. **Secrets hygiene** — confirm client secrets/connection strings are
   referenced by config key only, sourced from Key Vault or environment,
   never present in source or specs.

Report each item as confirmed / gap found / unable to verify, with the
specific file backing the answer.
