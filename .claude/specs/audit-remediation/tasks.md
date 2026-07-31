# Audit Remediation — Task Assignments

Generated: 2026-07-30

Refer to `spec.md` for full finding descriptions and priorities.

---

## Phase 1 — Safety-Critical (P0)

### Task P1.1: Fix Fire-and-Forget Dispatch Pipeline
**Owner**: `dotnet-backend`
**Priority**: P0
**Complexity**: L
**Files**: `CodeCallDispatchService.cs`, `ICodeCallDispatchService.cs`, `PhoneTreeEventsController.cs`, new `Services/Dispatch/DispatchJobQueue.cs` or similar

**What to do**:
- Replace `Task.Run` in `CodeCallDispatchService.DispatchIncidentAsync` with a channel-based `IHostedService` queue
- Add a `DispatchJob` record with status tracking (Queued, Running, Completed, Failed)
- Add a `GET /api/phone-trees/events/{id}/dispatch-status` endpoint to query pipeline state
- Add cancellation token support throughout the pipeline
- Add retry logic for transient failures (at least 1 retry per channel)
- Ensure exceptions in the pipeline are captured in the status record, not just logged
- `hipaa-checklist` review required (dispatch path is safety-critical)

**References**: `baseline-backend.md` Finding #6 (HIGH), `spec.md` F1

### Task P1.2: Add Missing [Authorize] on CodeCallLocationsController
**Owner**: `dotnet-backend`
**Priority**: P0
**Complexity**: XS
**Files**: `CodeCallLocationsController.cs`

**What to do**:
- Add `[Authorize(Policy = "RequireDirectoryRead")]` at class level on `CodeCallLocationsController`
- The POST/PUT/DELETE endpoints already have per-action `[Authorize]` — they will continue to work alongside the class-level attribute

**References**: `baseline-backend.md` Finding #5 (HIGH), `spec.md` F2

### Task P1.3: Fix LocalAccount.Roles EF Core Mapping
**Owner**: `dotnet-backend`
**Priority**: P0
**Complexity**: XS
**Files**: `Data/AppDbContext.cs`

**What to do**:
- In `OnModelCreating`, add: `entity.Ignore(x => x.Roles)` for the `LocalAccount` entity
- This prevents EF Core from trying to map the computed `Roles` property (which serializes/deserializes from `RolesJson`) as a separate column
- Without this, EF Core will throw `InvalidOperationException` on any query involving LocalAccount

**References**: `baseline-backend.md` Finding #2 (HIGH), `spec.md` F3

### Task P1.4: Migrate SignalR to Group-Based Broadcasting
**Owner**: `dotnet-backend`
**Priority**: P0
**Complexity**: L
**Files**:
- `CodeCallDispatchService.cs` (line 285: `Clients.All` -> `Clients.Group($"tenant-{tenantId}")` or `Clients.Group($"dept-{departmentId}")`)
- `ScheduleController.cs` (lines 47, 82, 101, 119, 131, 156, 168, 183, 199, 215: 10x `Clients.All`)
- `PhoneTreesController.cs` (lines 43, 53, 62, 71, 81, 90, 99: 7x `Clients.All`)
- `PhoneTreeEventsController.cs` (lines 53, 68, 94, 130, 145, 162, 177, 189: 8x `Clients.All`)
- `AdminController.cs` (lines 53, 70, 91, 108, 141, 153, 170: 7x `Clients.All`)
- `EscalationController.cs` (lines 39, 52, 62, 86: 4x `Clients.All`)

**What to do**:
- Replace ALL `Clients.All.SendAsync(...)` with targeted group broadcasts:
  - For tenant-scoped entities (departments, employees, schedules, time-off): `Clients.Group($"tenant-{tenantId}")`
  - For department-scoped entities (phone trees within a department): `Clients.Group($"dept-{departmentId}")`
  - For dispatch events: `Clients.Group($"tenant-{tenantId}")` (dispatch data is sensitive)
- Each controller method must resolve the tenant/department scope before broadcasting
- Keep `OnConnectedAsync` group-join logic as-is (already auto-joins tenant and department groups)
- `hipaa-checklist` review required (dispatch + PHI data exposure)

**References**: `baseline-backend.md` Finding #10 (MEDIUM), `spec.md` F4

---

## Phase 2 — Tenant Isolation & Auth Robustness (P0-P1)

### Task P2.1: Add Global Tenant Query Filter
**Owner**: `dotnet-backend`
**Priority**: P0
**Complexity**: L
**Files**: `Data/AppDbContext.cs`, `Services/ITenantContextService.cs`, `Middleware/TenantClaimsMiddleware.cs`

**What to do**:
- Add `HasQueryFilter(e => e.TenantId == _currentTenantId)` to all entities with `TenantId` in `OnModelCreating`
- For entities without direct `TenantId` (Shift, PhoneTreeEvent, DispatchStep, TimeOff), add the filter via navigation chain or denormalize `TenantId` first
- Allow super-admin override via `.IgnoreQueryFilters()` in controllers that need cross-tenant access
- Ensure the filter doesn't break global departments (TenantId = null) — handle null TenantId in the filter expression
- `hipaa-checklist` review required (tenant isolation is a PHI boundary)

**References**: `baseline-backend.md` Finding #1 (HIGH), `spec.md` F5

### Task P2.2: Fix Single-Resource Tenant Scoping
**Owner**: `dotnet-backend`
**Priority**: P1
**Complexity**: S
**Files**: `Controllers/DepartmentsController.cs`, `Controllers/CodeCallLocationsController.cs`

**What to do**:
- `DepartmentsController.Get(int id)`: Add tenant check after `FindAsync` — if user is not super-admin and department's TenantId is not in user's authorized tenants, return 403
- `CodeCallLocationsController.Get(int id)`: Same pattern — verify tenant authorization before returning the resource
- Consider whether to use `_tenantContext.IsSuperAdmin(User)` pattern or the new global query filter (P2.1)

**References**: `baseline-backend.md` Finding #4 (HIGH), `spec.md` F11

### Task P2.3: Fix access_as_user Scope Injection Pattern
**Owner**: `entra-identity`
**Priority**: P1
**Complexity**: S
**Files**: `Middleware/JwtValidationMiddleware.cs`, `Program.cs`

**What to do**:
- Stop injecting `scp: access_as_user` in Google and Local `OnTokenValidated` events
- Instead, have `JwtValidationMiddleware` accept that for Google and Local tokens, the authenticated state (post-OnTokenValidated) is sufficient without the scope claim
- OR: add a separate claim (`auth_validated: true`) set by each provider's `OnTokenValidated` and check that instead
- The key principle: the middleware should not check a claim that the pipeline itself injects
- `hipaa-checklist` review required (auth pipeline is security-critical)

**References**: `baseline-auth.md` Finding G6, `spec.md` F6

### Task P2.4: Add Google Auth Token Refresh
**Owner**: `entra-identity` + `react-frontend`
**Priority**: P1
**Complexity**: S
**Files**: `src/services/auth/googleAuthProvider.ts`, `src/hooks/useAuth.ts`

**What to do**:
- In `googleAuthProvider.ts`: detect when the stored Google ID token is expired (decode JWT, check exp claim)
- On expiry: redirect to Google sign-in for a new token, or use Google Identity Services' built-in token refresh (if available for ID tokens)
- In `useAuth.ts`: add a `refreshToken()` method called before API requests when the token is expired
- Fall back to silent re-auth; if that fails, force sign-out
- Add this is NOT a backend-facing fix — the backend already validates Google tokens correctly

**References**: `baseline-auth.md` Finding G3, `spec.md` F7

### Task P2.5: Add Explicit Graph API Scopes + Health Check
**Owner**: `entra-identity`
**Priority**: P1
**Complexity**: S
**Files**: `Services/GraphApiService.cs`, `Configuration/GraphApiOptions.cs`, `Program.cs`

**What to do**:
- Add a `Scopes` property to `GraphApiOptions` (default: `["https://graph.microsoft.com/.default"]`)
- In `GraphApiService.GetClient()`, pass `.WithScopes(_options.Value.Scopes)` when creating the credential
- Add a startup health check: on app start, call `GET /users?$top=1` to verify Graph credentials work
  - Log a structured warning on failure, don't crash startup (graceful degradation)
- Optionally add a `GET /api/integrations/graph/health` endpoint for operational monitoring
- `hipaa-checklist` review required (Graph API sync touches PHI)

**References**: `baseline-auth.md` Findings G1, G2, `spec.md` F8

### Task P2.6: Standardize auth_provider Claim
**Owner**: `entra-identity`
**Priority**: P1
**Complexity**: S
**Files**: `Program.cs`, `Middleware/JwtValidationMiddleware.cs`

**What to do**:
- Ensure Microsoft.Identity.Web tokens also get an explicit `auth_provider: microsoft` claim in `OnTokenValidated`
- Currently Google and Local set it, but Microsoft tokens don't — `JwtValidationMiddleware` treats null as "microsoft" implicitly
- Standardize to all three providers always setting `auth_provider`
- This prevents a future provider from accidentally falling through to Microsoft tenant validation

**References**: `baseline-auth.md` Finding G10, `spec.md` F9

### Task P2.7: Improve TenantClaimsMiddleware Error Handling
**Owner**: `dotnet-backend`
**Priority**: P1
**Complexity**: M
**Files**: `Middleware/TenantClaimsMiddleware.cs`

**What to do**:
- Keep graceful degradation (don't crash when tables don't exist — supports zero-downtime deploys)
- Add a structured warning log with correlation ID when the DB query fails
- Add a claims indicator (`tenant_claims_available: false`) on the `HttpContext.Items` so downstream middleware/controllers can detect when tenant scoping didn't load
- Optionally add a config flag `TenantScoping:Required` that, when true, returns 403 if tenant claims can't be loaded (for production deployments after migration is confirmed)
- `hipaa-checklist` review required (tenant claims are a PHI access control boundary)

**References**: `baseline-auth.md` Finding G9, `spec.md` F10

### Task P2.8: Fix Seed Data Tenant Affiliation
**Owner**: `dotnet-backend`
**Priority**: P1
**Complexity**: XS
**Files**: `Data/AppDbContext.cs`

**What to do**:
- Either: assign the two seed departments (Operations, Legal & Compliance) to `TenantId = 1` (matching the dev tenant)
- Or: document that `TenantId = null` departments are super-admin-only and add an `[Authorize]` check in the Department GET endpoints to enforce this
- **User decision needed**: confirm which approach

**References**: `baseline-backend.md` Finding #9 (MEDIUM), `spec.md` F12

---

## Phase 3 — Testing & Code Quality (P1)

### Task P3.1: Create Test Project
**Owner**: `dotnet-backend`
**Priority**: P1
**Complexity**: XL
**Files**: New `OnCallApi.Tests/` project

**What to do**:
- Create `OnCallApi.Tests` (xUnit project) in `src/backend/`
- Update `OnCallApi.sln` to include the test project
- Add NuGet packages: `xUnit`, `NSubstitute`, `Microsoft.EntityFrameworkCore.InMemory`, `FluentAssertions` (optional)
- Add a `TestBase` class with helpers: `CreateInMemoryDbContext()`, `CreateMockUser()`, `CreateTestTenant()`
- **User decision needed**: Confirm xUnit + NSubstitute + EF Core InMemory stack

**References**: `baseline-backend.md` Finding #15 (LOW), `spec.md` F13

### Task P3.2: Write Integration Tests
**Owner**: `dotnet-backend`
**Priority**: P1
**Complexity**: L
**Files**: `OnCallApi.Tests/`

**What to do**:
- Auth middleware tests: valid/invalid tokens, missing scope, expired, wrong tenant
- JwtValidationMiddleware tests: protected vs unprotected paths
- Tenant isolation tests: user from tenant A cannot access tenant B data
- CodeCallDispatchService tests: pipeline with mocked dispatch clients
- At least 5-10 tests covering the safety-critical paths

**References**: `spec.md` F13 (sub-task)

### Task P3.3: Add FluentValidation for All Models
**Owner**: `dotnet-backend`
**Priority**: P1
**Complexity**: M
**Files**: New validators in `Validation/` directory

**What to do**:
- Audit which models are missing validators
- Create validators for: `PhoneTree`, `PhoneTreeNode`, `PhoneTreeEvent`, `CodeCallLocation`, `Employee`, `Department`, `Shift`, `EscalationPolicy`, `Tenant`, `TenantAdmin`
- Register them via `AddValidatorsFromAssemblyContaining` (already set up in Program.cs, likely just need to create the classes)
- Ensure PHI fields have length/max constraints

**References**: `baseline-backend.md` Finding #16 (LOW), `spec.md` F22

### Task P3.4: Consolidate Duplicate Endpoints
**Owner**: `dotnet-backend`
**Priority**: P1
**Complexity**: M
**Files**: `Controllers/DirectoryController.cs`, `Controllers/PhoneTreesController.cs`, `Controllers/AdminController.cs`, `Controllers/DepartmentsController.cs`

**What to do**:
- Phone tree GETs: remove from `DirectoryController`, keep in `PhoneTreesController` (more specific)
- Department GETs: remove from `AdminController`, keep in `DepartmentsController`
- On-call lookups: remove from `DirectoryController`, keep in `ScheduleController`
- Add `[Obsolete]` on removed routes with redirect for one release cycle before removal
- Update frontend API calls if they reference the removed routes

**References**: `baseline-backend.md` Finding #14 (LOW), `spec.md` F14

### Task P3.5: Fix UpdateDepartmentRequest Missing AzureAdGroupId
**Owner**: `dotnet-backend`
**Priority**: P1
**Complexity**: XS
**Files**: Request DTOs, `AdminController.cs`

**What to do**:
- Add `AzureAdGroupId` property to `UpdateDepartmentRequest` DTO
- Update the `AdminController.UpdateDepartment` handler to copy `AzureAdGroupId` from request to entity

**References**: `baseline-backend.md` Finding #19 (LOW), `spec.md` F15

---

## Phase 4 — Cleanup & Consistency (P2)

### Task P4.1: DutyHourRule/Violation OnModelCreating Config
**Owner**: `dotnet-backend`
**Priority**: P2
**Complexity**: XS
**Files**: `Data/AppDbContext.cs`

**What to do**: Add FK and relationship configuration for `DutyHourRule` and `DutyHourViolation` entities.

### Task P4.2: Consistent DI Patterns
**Owner**: `dotnet-backend`
**Priority**: P2
**Complexity**: M
**Files**: `Controllers/DepartmentsController.cs`, `Controllers/TenantsController.cs`, `Controllers/TenantAdminsController.cs`, `Controllers/SettingsController.cs`, `Controllers/CodeCallLocationsController.cs`

**What to do**: Replace direct `AppDbContext` injection with service interfaces. Extract service classes where needed.

### Task P4.3: Denormalize TenantId on Deep-Chain Entities
**Owner**: `dotnet-backend`
**Priority**: P2
**Complexity**: L
**Files**: Model files (`Shift.cs`, `PhoneTreeEvent.cs`, `DispatchStep.cs`, `TimeOff.cs`), `AppDbContext.cs`, new migration

**What to do**: Add `TenantId` directly to entities that currently require 3-5 joins to reach Tenant. Update query filters and seed data.

### Task P4.4: Fix Nullable Navigation Inconsistencies
**Owner**: `dotnet-backend`
**Priority**: P2
**Complexity**: S
**Files**: Model files

**What to do**: Audit all navigation properties. Use `= null!` pattern consistently where FK is non-nullable.

### Task P4.5: Fix ImportController Error Codes
**Owner**: `dotnet-backend`
**Priority**: P2
**Complexity**: XS
**Files**: `Controllers/ImportController.cs`

**What to do**: Return appropriate HTTP status codes (400, 422) instead of wrapping all errors in 200 OK.

### Task P4.6: Strongly-Typed SignalR Hub Interface
**Owner**: `dotnet-backend`
**Priority**: P2
**Complexity**: S
**Files**: `Hubs/OnCallNotificationHub.cs`, new `Hubs/INotificationClient.cs`

**What to do**: Define a client interface with strongly-typed method signatures. Add method name constants to replace string literals.

### Task P4.7: Add DELETE Endpoint on SettingsController
**Owner**: `dotnet-backend`
**Priority**: P2
**Complexity**: XS
**Files**: `Controllers/SettingsController.cs`

### Task P4.8: Disambiguate Overlapping Route Namespaces
**Owner**: `dotnet-backend`
**Priority**: P2
**Complexity**: XS
**Files**: Controller route attributes

**What to do**: Add unique route prefixes to avoid ASP.NET route merging confusion.

### Task P4.9: Replace Magic String Enum Fields
**Owner**: `dotnet-backend`
**Priority**: P2
**Complexity**: M
**Files**: Model files, `AppDbContext.cs`

**What to do**: Replace string fields like `Shift.Tier`, `PhoneTree.TreeType`, `PhoneTreeEvent.Status`, `DispatchStep.Status`, `DispatchStep.StepKey`, `TimeOff.Type`, `TimeOff.Status`, `ShiftSwap.Status`, `TenantAdmin.Role`, `Employee.Presence` with proper enums. Add EF Core value converters.

### Task P4.10: Remove Dead Code (getAllProviders)
**Owner**: `react-frontend`
**Priority**: P2
**Complexity**: XS
**Files**: `src/services/auth/authFactory.ts`

**What to do**: Remove the `getAllProviders()` function and its surrounding code.
