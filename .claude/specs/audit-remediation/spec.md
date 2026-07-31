# Audit Remediation: Comprehensive Implementation Plan

**Date**: 2026-07-30
**Source baselines**: `baseline-auth.md` (10 gaps), `baseline-backend.md` (22 items, 6 HIGH)
**Status**: Plan — not yet implemented
**Review gates required**: `code-review` on every task, `hipaa-checklist` on PHI/dispatch/auth tasks

---

## Priority Key

| Priority | Meaning | Count |
|----------|---------|-------|
| **P0** | Safety-critical or security-breaching; must fix before next production deploy | 5 |
| **P1** | Correctness or reliability; should fix in current sprint | 10 |
| **P2** | Cleanup, consistency, technical debt; schedule when capacity allows | 14 |

---

## Consolidated Findings Map

Each finding below maps to its source baseline(s) and the priority assigned.

| ID | Finding | Source | Priority | Phase | Complexity |
|----|---------|--------|----------|-------|------------|
| F1 | Fire-and-forget dispatch pipeline: `Task.Run` with no cancellation, no recovery, no status tracking | backend-H6 | **P0** | 1 | L |
| F2 | CodeCallLocationsController has no `[Authorize]` on GET endpoints — anonymous access to code-call locations | backend-H5 | **P0** | 1 | XS |
| F3 | `LocalAccount.Roles` computed property will cause EF Core runtime error — missing `.Ignore(x => x.Roles)` | backend-H2 | **P0** | 1 | XS |
| F4 | SignalR broadcasts use `Clients.All` everywhere — dispatch data, incidents, employee PHI go to every connected client | backend-M10 | **P0** | 1 | L |
| F5 | No global tenant query filter — tenant isolation is ad-hoc and inconsistently applied | backend-H1 | **P0** | 2 | L |
| F6 | `access_as_user` scope self-injected in Google/Local `OnTokenValidated` — circular dependency in auth pipeline | auth-G6 | **P1** | 2 | S |
| F7 | Google auth tokens never refresh — 1-hour ID token expiry breaks sessions silently | auth-G3 | **P1** | 2 | S |
| F8 | Graph API uses implicit `.default` scopes — no explicit scopes configured, no startup health check | auth-G1, auth-G2 | **P1** | 2 | S |
| F9 | `auth_provider` claim used inconsistently — only Google/Local set it; Microsoft tokens implicitly fall through | auth-G10 | **P1** | 2 | S |
| F10 | TenantClaimsMiddleware silently swallows DB errors — tenant scoping invisible until migrations run | auth-G9 | **P1** | 2 | M |
| F11 | Missing tenant scoping on single-resource GET endpoints (Department by ID, CodeCallLocation by ID) | backend-H4 | **P1** | 2 | S |
| F12 | Seed data creates 2 global departments with `TenantId = null` — no tenant affiliation, bypasses filtering | backend-M9 | **P1** | 2 | XS |
| F13 | No test project exists anywhere in the solution — cannot validate any fix | backend-L15 | **P1** | 3 | XL |
| F14 | Duplicate endpoints: phone tree GETs on both DirectoryController and PhoneTreesController; department GETs on both DepartmentsController and AdminController | backend-L14 | **P1** | 3 | M |
| F15 | `UpdateDepartmentRequest` missing `AzureAdGroupId` field — can set on create but not update | backend-L19 | **P1** | 3 | XS |
| F16 | DutyHourRule / DutyHourViolation have no `OnModelCreating` configuration — EF conventions may misbehave | backend-H3 | **P2** | 4 | XS |
| F17 | Inconsistent DI patterns: controllers use interfaces, concrete classes, or direct DbContext arbitrarily | backend-M7 | **P2** | 4 | M |
| F18 | Deep tenant resolution chains (4-5 joins from Shift/Event/TimeOff to Tenant) | backend-M8 | **P2** | 4 | L |
| F19 | Nullable navigation properties inconsistent — some entities use `= null!`, others have nullable FK + nullable nav | backend-M11 | **P2** | 4 | S |
| F20 | ImportController wraps all errors in 200 OK — hides programming errors | backend-L12 | **P2** | 4 | XS |
| F21 | No server-to-client SignalR methods defined on hub class — all broadcasting is stringly-typed via service classes | backend-L13 | **P2** | 4 | S |
| F22 | FluentValidation not registered for all models (only `ScheduleValidator` in assembly scan) | backend-L16 | **P2** | 4 | M |
| F23 | No DELETE endpoint on SettingsController | backend-L17 | **P2** | 4 | XS |
| F24 | Overlapping route namespaces: `PhoneTreesController` and `PhoneTreeEventsController` both use `[Route("api/phone-trees")]` | backend-L18 | **P2** | 4 | XS |
| F25 | AuditLog has no FK relationships — loose columns with no referential integrity | backend-L20 | **P2** | 4 | XS |
| F26 | Long PK chain: AuditLog uses `long` ID while all other entities use `int` or `Guid` | backend-L21 | **P2** | 4 | XS |
| F27 | Magic string enum fields: `Shift.Tier`, `PhoneTree.TreeType`, `DispatchStep.Status`, `TimeOff.Type`, etc. | backend-L22 | **P2** | 4 | M |
| F28 | `getAllProviders()` in authFactory is dead code — never called | auth-G7 | **P2** | 4 | XS |

---

## Phase Breakdown

### Phase 1: Safety-Critical (P0) — Estimated: 3-4 days

Goal: Close the security gaps and safety-critical reliability issues before any other work.

| Task | Owner | Files to change | Complexity |
|------|-------|-----------------|------------|
| 1.1 Fix dispatch pipeline: replace `Task.Run` with tracked background job, add cancellation, retry, status endpoint | `dotnet-backend` | `CodeCallDispatchService.cs`, `ICodeCallDispatchService.cs`, `PhoneTreeEventsController.cs`, new `BackgroundJobStore.cs` or use `IHostedService` queue | L |
| 1.2 Add `[Authorize]` to CodeCallLocationsController class-level | `dotnet-backend` | `CodeCallLocationsController.cs` | XS |
| 1.3 Add `.Ignore(x => x.Roles)` to LocalAccount in `AppDbContext.OnModelCreating` | `dotnet-backend` | `Data/AppDbContext.cs` | XS |
| 1.4 Migrate SignalR broadcasts to group-based targeting instead of `Clients.All` | `dotnet-backend` | `OnCallNotificationHub.cs`, `CodeCallDispatchService.cs`, `ScheduleController.cs`, `PhoneTreesController.cs`, `PhoneTreeEventsController.cs`, `AdminController.cs`, `EscalationController.cs` | L |

**Review gates**: `hipaa-checklist` on 1.1 and 1.4 (dispatch + PHI exposure); `code-review` on all.

### Phase 2: Tenant Isolation & Auth Robustness (P0-P1) — Estimated: 4-5 days

Goal: Ensure tenant data isolation is reliable, and auth pipeline has no silent failures.

| Task | Owner | Files to change | Complexity |
|------|-------|-----------------|------------|
| 2.1 Add global tenant query filter via `HasQueryFilter` in `AppDbContext.OnModelCreating` for all entities with TenantId | `dotnet-backend` | `Data/AppDbContext.cs`, possibly new `ITenantContextService` integration for runtime tenant ID | L |
| 2.2 Audit all single-resource GETs and add tenant filtering where missing (`DepartmentsController.Get(int id)`, `CodeCallLocationsController.Get(int id)`) | `dotnet-backend` | `Controllers/DepartmentsController.cs`, `Controllers/CodeCallLocationsController.cs` | S |
| 2.3 Fix `access_as_user` scope injection pattern — validate scope from token claims directly, not from self-injected `OnTokenValidated` | `entra-identity` | `Middleware/JwtValidationMiddleware.cs`, `Program.cs` (Google/Local `OnTokenValidated`) | S |
| 2.4 Add Google auth token refresh in frontend — detect expiry, redirect to re-auth or use silent refresh | `entra-identity` + `react-frontend` | `services/auth/googleAuthProvider.ts`, `hooks/useAuth.ts` | S |
| 2.5 Add explicit Graph API scopes to `GraphApiService` + startup connectivity health check | `entra-identity` | `Services/GraphApiService.cs`, `Configuration/GraphApiOptions.cs`, `Program.cs` | S |
| 2.6 Fix `auth_provider` claim — standardize across all three providers with consistent claim type | `entra-identity` | `Program.cs` (all three `OnTokenValidated`), `Middleware/JwtValidationMiddleware.cs` | S |
| 2.7 Improve TenantClaimsMiddleware error handling — still graceful for zero-downtime deploys but adds structured logging and a fallback indicator claim | `dotnet-backend` | `Middleware/TenantClaimsMiddleware.cs` | M |
| 2.8 Fix seed data: assign seed departments to a default tenant or make super-admin-only | `dotnet-backend` | `Data/AppDbContext.cs` (seed section) | XS |

**Review gates**: `hipaa-checklist` on 2.1, 2.2, 2.3, 2.7 (tenant isolation is PHI boundary); `entra-integration-audit` on 2.3-2.6; `code-review` on all.

### Phase 3: Testing Foundations & Code Quality (P1) — Estimated: 3-4 days

Goal: Make the codebase testable and eliminate the worst consistency problems.

| Task | Owner | Files to change | Complexity |
|------|-------|-----------------|------------|
| 3.1 Create test project with xUnit + EF Core in-memory + test patterns | `dotnet-backend` | New `OnCallApi.Tests/` project, `OnCallApi.sln` update | XL |
| 3.2 Write first integration tests: auth middleware, dispatch pipeline (happy path), tenant filtering | `dotnet-backend` | `OnCallApi.Tests/` — middleware tests, service tests | L |
| 3.3 Register FluentValidation for all model validators (assembly scan already set up, just create validator classes) | `dotnet-backend` | New validators in `Validation/` | M |
| 3.4 Consolidate duplicate endpoints — deprecate one of each duplicate pair, route consolidation | `dotnet-backend` | `Controllers/DirectoryController.cs`, `Controllers/PhoneTreesController.cs`, `Controllers/AdminController.cs`, `Controllers/DepartmentsController.cs` | M |
| 3.5 Add `AzureAdGroupId` to `UpdateDepartmentRequest` | `dotnet-backend` | Models/request DTOs, `AdminController.cs` update handler | XS |

**Review gates**: `code-review` on all.

### Phase 4: Cleanup & Consistency (P2) — Estimated: 3-5 days

Goal: Systematic cleanup of technical debt.

| Task | Owner | Files to change | Complexity |
|------|-------|-----------------|------------|
| 4.1 Add DutyHourRule/Violation FK configuration in `OnModelCreating` | `dotnet-backend` | `Data/AppDbContext.cs` | XS |
| 4.2 Refactor controllers to consistent DI pattern (all use interfaces) | `dotnet-backend` | `Controllers/DepartmentsController.cs`, `Controllers/TenantsController.cs`, `Controllers/TenantAdminsController.cs`, `Controllers/SettingsController.cs`, `Controllers/CodeCallLocationsController.cs` | M |
| 4.3 Add `TenantId` denormalization to deep-chain entities (Shift, PhoneTreeEvent, DispatchStep, TimeOff) — migration + query filter | `dotnet-backend` | Model files, `AppDbContext.cs`, migration | L |
| 4.4 Fix nullable navigation inconsistencies — use `= null!` pattern consistently | `dotnet-backend` | Model files | S |
| 4.5 Fix ImportController to return appropriate HTTP status codes on error | `dotnet-backend` | `Controllers/ImportController.cs` | XS |
| 4.6 Define strongly-typed SignalR hub interface + method constants | `dotnet-backend` | `Hubs/OnCallNotificationHub.cs`, new `Hubs/INotificationClient.cs` | S |
| 4.7 Add DELETE endpoint to SettingsController | `dotnet-backend` | `Controllers/SettingsController.cs` | XS |
| 4.8 Disambiguate overlapping route namespaces (PhoneTreesController vs PhoneTreeEventsController) | `dotnet-backend` | Controller route attributes | XS |
| 4.9 Replace magic string fields with enum types + EF conversion | `dotnet-backend` | Model files, `AppDbContext.cs` | M |
| 4.10 Remove dead code `getAllProviders()` in authFactory | `react-frontend` | `services/auth/authFactory.ts` | XS |

**Review gates**: `code-review` on all.

---

## Files with Changes Across Multiple Phases

| File | Phase 1 | Phase 2 | Phase 3 | Phase 4 | Total changes |
|------|---------|---------|---------|---------|---------------|
| `Program.cs` | | 2.3, 2.5, 2.6 | | | 3 |
| `AppDbContext.cs` | 1.3 | 2.1, 2.8 | | 4.1, 4.3, 4.9 | 6 |
| `CodeCallDispatchService.cs` | 1.1, 1.4 | | | | 2 |
| `OnCallNotificationHub.cs` | 1.4 | | | 4.6 | 2 |
| `CodeCallLocationsController.cs` | 1.2 | 2.2 | | 4.2 | 3 |
| `DepartmentsController.cs` | | 2.2 | | 4.2 | 2 |
| `JwtValidationMiddleware.cs` | | 2.3, 2.6 | | | 2 |
| `TenantClaimsMiddleware.cs` | | 2.7 | | | 1 |
| `GraphApiService.cs` | | 2.5 | | | 1 |
| `AdminController.cs` | 1.4 | | 3.5 | | 2 |
| `ScheduleController.cs` | 1.4 | | | | 1 |
| `PhoneTreesController.cs` | 1.4 | | 3.4 | | 2 |
| `PhoneTreeEventsController.cs` | 1.4 | | | | 1 |
| `EscalationController.cs` | 1.4 | | | | 1 |
| `authFactory.ts` | | | | 4.10 | 1 |
| `googleAuthProvider.ts` | | 2.4 | | | 1 |

---

## Dependency Graph

```
Phase 1 (P0)
  ├── 1.1 (dispatch pipeline) — independent
  ├── 1.2 (missing [Authorize]) — independent
  ├── 1.3 (LocalAccount .Ignore) — independent
  └── 1.4 (SignalR groups) — independent
       │
Phase 2 (P0-P1) [can start after 1.4 resolves any SignalR conflicts]
  ├── 2.1 (tenant query filter) — independent
  ├── 2.2 (single-resource tenant scoping) — depends on 2.1 pattern
  ├── 2.3 (scope injection fix) — independent
  ├── 2.4 (Google token refresh) — independent
  ├── 2.5 (Graph scopes + health) — independent
  ├── 2.6 (auth_provider standardize) — independent
  ├── 2.7 (TenantClaimsMiddleware) — independent
  └── 2.8 (seed data) — depends on 2.1 pattern
       │
Phase 3 (P1) [can start after Phase 2 is complete — tests validate tenant isolation]
  ├── 3.1 (test project) — independent
  ├── 3.2 (integration tests) — depends on 3.1
  ├── 3.3 (FluentValidation) — independent
  ├── 3.4 (duplicate endpoints) — independent
  └── 3.5 (UpdateDepartmentRequest) — independent
       │
Phase 4 (P2) [can start after Phase 3 is complete — tests catch regressions]
  ├── 4.1-4.10 — all independent of each other
```

---

## Escalation Points

These items require user decision before implementation:

1. **F1 (Dispatch pipeline rewrite)**: The current `Task.Run` approach is clearly broken for production use. Three possible replacements:
   - (a) `IHostedService` + channel queue — keeps in-process, survives request scope, adds tracking
   - (b) Azure Service Bus / Queue Storage — fully durable, survives restarts, adds infra dependency
   - (c) Hangfire / Quartz.NET — adds third-party dependency but gives retries, scheduling, dashboard
   
   **Recommendation**: (a) for now (minimal infra change), with a path to (b) later. **User decision needed.**

2. **F4 (SignalR broadcasts)**: All 30+ `Clients.All` calls need to become tenant-group or department-group broadcasts. The hub already auto-joins users to `dept-{id}` and `tenant-{id}` groups. However, several broadcasts (e.g., `TimeOffUpdated`, `PhoneTreeCreated`) have no natural tenant scope in the current code path — the controller would need to resolve the tenant ID before broadcasting.
   
   **Question**: Should broadcasts default to the current user's tenant group, or should some events (global admin events) still go to all clients?

3. **F5 (Global tenant query filter)**: Adding `HasQueryFilter` to entities with `TenantId` will change query behavior globally. Some queries that intentionally bypass tenant scoping (e.g., super-admin cross-tenant views) would break. Need a `.IgnoreQueryFilters()` pattern for super-admin endpoints.
   
   **Question**: Confirm that super-admins should see all tenants, and all other users see only their authorized tenants.

4. **F12 (Seed data)**: Two global departments (Operations, Legal & Compliance) seeded with `TenantId = null`. Should they belong to a specific tenant, or remain global/super-admin-only?

5. **F13 (Test project)**: Creating the test project needs agreement on:
   - Test framework: xUnit (most common for .NET)
   - Database: SQLite in-memory vs EF Core InMemory provider vs TestContainers
   - Mocking: NSubstitute vs Moq vs manual stubs
   
   **Recommendation**: xUnit + EF Core InMemory + NSubstitute. **User decision needed.**

---

## Open Questions Not Yet Addressed

From the baseline documents, these were identified but not included in the phased plan because they require architectural decisions:

1. **Multi-tenant scope for dev mode**: Dev mode hardcodes `TenantId:1`. When multiple tenants exist in dev, this breaks. Should dev mode support tenant switching?

2. **EscalationBackgroundService**: Runs every 2 minutes with no way to disable in dev. Should follow the sync-service pattern (configurable interval, 0 = disabled).

3. **CalendarSyncService re-syncs all unsynced shifts**: No tracking of which shifts have been synced. Could duplicate calendar entries. Needs a `LastSyncedAt` tracking mechanism.

4. **TeamsBotService usage**: Injected but its usage path was not traced. Needs clarification on whether it's actively used or dead code.

5. **AuditLog encryption**: PHI (UserName, IpAddress, Details) stored in plaintext. Column-level encryption (Always Encrypted) was mentioned in the architecture but not implemented.

6. **API versioning**: No versioning strategy. Acceptable for current scale but should be planned.
