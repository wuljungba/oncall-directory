# Backend Baseline Discovery

Generated: 2026-07-30

## Overview

The backend is an ASP.NET Core 8 Web API (`OnCallApi`) for a hospital on-call scheduling and emergency dispatch system. It provides REST endpoints consumed by a React SPA frontend and communicates with Microsoft 365 (Graph API) for user/calendar/presence sync, Teams notifications, and SharePoint publishing. It also integrates with Cisco CUCM, Singlewire InformaCast, and Stryker Vocera for emergency code-call dispatch over hospital communication infrastructure.

---

## File/Component Map

### Controllers (REST API entry points)

| Controller | Route Prefix | DI Style | Class Auth | Scope |
|---|---|---|---|---|
| `ScheduleController` | `/api/schedule` | Interface (`IScheduleService`) | `RequireScheduleRead` | Scheduling CRUD, shift assignment, time-off, swaps |
| `DepartmentsController` | `/api/departments` | Direct `AppDbContext` | `RequireDirectoryRead` | List departments with tenant scoping |
| `DirectoryController` | `/api/directory` | Interface (`IDirectoryService`) | `RequireDirectoryRead` | Employee directory, phone tree read, on-call lookups |
| `PhoneTreesController` | `/api/phone-trees` | Interface (`IDirectoryService`) | `RequireDirectoryRead` | Phone tree CRUD, node management |
| `PhoneTreeEventsController` | `/api/phone-trees` | Interface (`IPhoneTreeEventService`) | `RequireDirectoryRead` | Emergency event lifecycle, dispatch pipeline trigger |
| `EscalationController` | `/api/escalation` | Concrete class (`EscalationService`) | `RequireScheduleRead` | Escalation policies and event management |
| `ComplianceController` | `/api/compliance` | Interface (`IDutyHourService`) | `RequireScheduleRead` | Duty hour rules, compliance checks |
| `TenantsController` | `/api/tenants` | Direct `AppDbContext` | `RequireTenantManage` | Tenant CRUD (multi-tenant management) |
| `TenantAdminsController` | `/api/tenants/{tenantId}/admins` | Direct `AppDbContext` | `RequireTenantManage` | Tenant admin assignment |
| `AdminController` | `/api/admin` | Interface (`IAdminService`) | None (per-action) | Employee/department management |
| `SettingsController` | `/api/settings` | Direct `AppDbContext` | `RequireScheduleRead` | Key-value app settings |
| `ImportController` | `/api/import` | Concrete class (`BulkImportService`) | `RequireAdminFull` | CSV bulk import (employees, shifts) |
| `IntegrationsController` | `/api/integrations` | Interface (`IGraphApiService`) | `RequireScheduleRead` | M365 integration triggers |
| `CodeCallLocationsController` | `/api/code-call-locations` | Direct `AppDbContext` | **None at class level** | Hospital code call location management |
| `IntegrationDiagnosticsController` | `/api/integrations/test` | Interface (`ICodeCallDispatchService`) | `RequireAdminFull` | Dispatch channel connectivity tests |

### Services (Business Logic)

| Service | Pattern | Key Dependencies | Scope |
|---|---|---|---|
| `ScheduleService` | `IScheduleService` | `AppDbContext`, `TeamsNotificationService?` | Scoped |
| `DirectoryService` | `IDirectoryService` | `AppDbContext` | Scoped |
| `AdminService` | `IAdminService` | `AppDbContext`, `ITenantContextService`, `IHttpContextAccessor` | Scoped |
| `EscalationService` | No interface | `AppDbContext`, `TeamsNotificationService?` | Scoped |
| `EscalationBackgroundService` | `BackgroundService` | `IServiceProvider`, timer every 2 min | Singleton |
| `DutyHourService` | `IDutyHourService` | `AppDbContext` | Scoped |
| `PhoneTreeEventService` | `IPhoneTreeEventService` | `AppDbContext` | Scoped |
| `CodeCallDispatchService` | `ICodeCallDispatchService` | `IServiceScopeFactory`, `IHubContext`, `DispatchOptions` | Scoped |
| `AuditService` | `IAuditService` (singleton) | Channel-based bounded queue (2000 cap, DropOldest) | Singleton |
| `AuditBackgroundService` | `BackgroundService` | Channel reader, batch flush every 5s/100 entries | Singleton |
| `BulkImportService` | No interface | `AppDbContext` | Scoped |
| `TenantContextService` | `ITenantContextService` | `AppDbContext`, `IHttpContextAccessor` | Scoped |
| `TenantSyncService` | No interface | `AppDbContext`, `IGraphApiService` | Scoped |
| `GraphApiService` | `IGraphApiService` | `IOptions<GraphApiOptions>`, lazy-init `ClientSecretCredential` | Scoped |
| `LocalAccountService` | `ILocalAccountService` | `AppDbContext`, `LocalJwtService` | Scoped |
| `TeamsNotificationService` | No interface | `IGraphApiService` | Scoped |
| `SharePointPublishingService` | No interface | `IServiceProvider`, `IConfiguration` | Scoped |
| `TeamsBotService` | No interface | (not read in detail) | Scoped |
| `AvailabilityService` | No interface | `IServiceProvider` | Scoped |
| `AdSyncBackgroundService` | `BackgroundService` | `IServiceProvider`, configurable interval (dev: 0 = disabled) | Singleton |
| `CalendarSyncService` | `BackgroundService` | `IServiceProvider`, configurable interval | Singleton |
| `DepartmentSyncService` | `BackgroundService` | `IServiceProvider`, configurable interval (default 360 min) | Singleton |
| `PresenceSyncService` | `BackgroundService` | `IServiceProvider`, configurable interval (dev: 2 min) | Singleton |

### Dispatch Clients (Emergency Communication)

| Client | Target System | Protocol | Config Section |
|---|---|---|---|
| `CiscoCucmClient` | Cisco Unified CM | AXL SOAP (XML over HTTP) | `Dispatch:Cucm` |
| `InformaCastClient` | Singlewire InformaCast Fusion | REST JSON | `Dispatch:InformaCast` |
| `VoceraClient` | Stryker Vocera VMP | SOAP + REST | `Dispatch:Vocera` |

### Models (30 entities/models)

| Entity | PK Type | TenantId | PHI-Bearing | Notes |
|---|---|---|---|---|
| `Employee` | `Guid` | `int?` | YES (name, email, phone, location) | Core personnel record |
| `Department` | `int` | `int?` | Minimal | Can be global (TenantId=null) |
| `Schedule` | `int` | -- (via Department) | Indirect | No direct TenantId |
| `Shift` | `int` | -- (via Schedule) | Indirect | No direct TenantId |
| `ShiftSwap` | `int` | -- (via Shift) | Indirect | Reason field |
| `TimeOff` | `int` | -- (via Employee) | YES (dates, type, notes) | Type includes "sick", "cme" |
| `PhoneTree` | `int` | -- (via Department) | Indirect | Procedure/fallback text |
| `PhoneTreeNode` | `int` | -- (via PhoneTree) | Indirect | |
| `PhoneTreeEvent` | `int` | -- (via PhoneTree) | YES (location, notes, debrief) | Free-text fields |
| `PhoneTreeEventParticipant` | `int` | -- (via Event) | Indirect | |
| `DispatchStep` | `int` | -- (via Event) | Indirect | Detail free-text |
| `EscalationPolicy` | `int` | -- (via Department) | No | |
| `EscalationEvent` | `int` | -- (via Policy) | YES (Details free-text) | |
| `DutyHourRule` | `int` | -- (via Department) | No | |
| `DutyHourViolation` | `int` | No | YES (Description free-text) | |
| `CodeCallLocation` | `int` | -- (via Department) | No | |
| `Tenant` | `int` | Self | Minimal | ContactEmail |
| `TenantAdmin` | `int` | `int` (required) | PII (AzureAdObjectId) | |
| `AuditLog` | `long` | `int?` | YES (UserName, IpAddress) | No FK relationships |
| `AppSetting` | `string` | `int?` | Depends on value | Key-value store |
| `LocalAccount` | `int` | No | YES (email, password hash) | Missing `.Ignore(Roles)` |

### Middleware Pipeline (order in Program.cs)

1. `ExceptionHandlingMiddleware` -- Catches unhandled exceptions, returns structured JSON
2. Response compression
3. Rate limiter (100 req/min)
4. HTTPS redirection
5. CORS
6. Authentication
7. Authorization
8. `JwtValidationMiddleware` -- Validates scope, user ID, tenant ID on protected endpoints
9. `TenantClaimsMiddleware` -- Expands claims from TenantAdmin records, lazy auto-assignment
10. `HipaaAuditMiddleware` -- Queues audit logs for PHI-accessing endpoints

### Real-Time Communication

- SignalR hub at `/hubs/notifications` (`OnCallNotificationHub`)
- Authorized; auto-joins department and tenant groups on connect
- Server broadcasts via `IHubContext<OnCallNotificationHub>` (all clients, no filtering)
- No server-to-client method definitions in the hub class

---

## Traced End-to-End Flows

### Flow 1: Schedule Read

```
Frontend GET /api/schedule?departmentId=5
  → JwtValidationMiddleware validates scope/user
  → TenantClaimsMiddleware expands tenant claims
  → HipaaAuditMiddleware queues audit log
  → ScheduleController.GetAll(departmentId)
    → ScheduleService.GetSchedulesAsync(5)
      → AppDbContext.Schedules.Where(d => DepartmentId == 5)
        .Include(s => s.Department).OrderByDescending(CreatedAt)
      → returns List<Schedule>
    → returns Ok(schedules)
```

**Observation**: No tenant filtering on this query. Any authenticated user can read schedules for any department, regardless of tenant assignment. The controller delegates all filtering to the service layer which does not filter by tenant.

### Flow 2: Code-Call Dispatch

```
Frontend POST /api/phone-trees/{treeId}/events  (body: { location, notes, ... })
  → PhoneTreeEventsController.CreateEvent(treeId, request)
    → PhoneTreeEventService.CreateEventAsync(evt)  -- saves as "active"
    → CodeCallDispatchService.DispatchIncidentAsync(evt, treeType)
      → Fire-and-forget Task.Run (no cancellation, no tracking)
        → Step 1: CiscoCucmClient - AXL SOAP device registration check + page
        → Step 2: InformaCastClient - REST API scenario trigger
        → Step 3: VoceraClient - SOAP badge alert
        → Step 4: SIP PBX fallback if all primary channels fail
        → Each step records DispatchStep to DB + broadcasts via SignalR
        → On success: auto-acknowledge via PhoneTreeEventService
    → returns Ok(created event)
```

**Observation**: The dispatch pipeline runs in a fire-and-forget `Task.Run`, which means:
- If the HTTP request completes before dispatch finishes, exceptions are silently swallowed
- No way to track pipeline status from the request lifecycle
- The dispatch pipeline creates its own DI scopes to resolve clients, bypassing the request scope
- If the app shuts down during dispatch, the operation is lost

### Flow 3: Escalation Check

```
EscalationBackgroundService (every 2 minutes)
  → EscalationService.CheckAndEscalateAsync()
    → Load all active EscalationPolicies
    → For each policy: find active shifts in department
    → For each shift: check if escalation events exist
      → If no event and shift started > MaxResponseMinutes ago: fire tier 1
      → If event exists and time since trigger > MaxResponseMinutes: fire next tier
    → FireEscalation: creates EscalationEvent, sends Teams notification
```

**Observation**: No tenant filtering on escalation policies or active shifts. The background service loads all policies and all active shifts, which could become expensive as data grows.

---

## Gaps, TODOs, and Inconsistencies

### HIGH SEVERITY

1. **No global tenant query filter** in `AppDbContext.OnModelCreating`. No entity has `.HasQueryFilter()` for tenant isolation. All tenant filtering relies on application-level code, which is inconsistently applied. Several queries (ScheduleService, DirectoryService, EscalationService, DutyHourService) do NOT filter by tenant at all.

2. **Missing `.Ignore(x => x.Roles)` on LocalAccount entity**. The `Roles` computed property (getter deserializes from RolesJson, setter serializes) will be mapped by EF Core as a column, which will cause a runtime `InvalidOperationException` or create an unintended column.

3. **DutyHourRule and DutyHourViolation have no OnModelCreating configuration** whatsoever -- no FK relationships defined, relying entirely on EF Core conventions. This could produce unexpected cascade behaviors.

4. **Missing tenant scoping on single-resource GETs**. `DepartmentsController.Get(int id)` and `CodeCallLocationsController.Get(int id)` do not apply tenant filtering, so scoping is only enforced on LIST operations. Any authenticated user can look up any department or code-call location by ID.

5. **CodeCallLocationsController has no class-level [Authorize]** attribute. Its GET endpoints have no authorization checks at all (no class-level attribute, no per-action attribute). This is the only controller in this state.

6. **Fire-and-forget dispatch pipeline** in `CodeCallDispatchService.DispatchIncidentAsync`. Uses `Task.Run` with no cancellation token, no exception handling beyond catch-and-log, and no retry mechanism. If the pipeline fails mid-way, the caller gets no indication and no recovery is attempted.

### MEDIUM SEVERITY

7. **Inconsistent DI patterns**: Some controllers use interfaces (`IScheduleService`, `IDirectoryService`), others use concrete classes (`EscalationService`, `BulkImportService`), and others use direct `AppDbContext` (`DepartmentsController`, `TenantsController`, `TenantAdminsController`, `SettingsController`, `CodeCallLocationsController`).

8. **Deep tenant resolution chains**. Many entities have no direct `TenantId` and rely on navigation chains: `Shift → Schedule → Department → Tenant` (4 joins), `DispatchStep → PhoneTreeEvent → PhoneTree → Department → Tenant` (5 joins), `TimeOff → Employee → Department → Tenant` (3 joins). This makes tenant-isolated queries expensive and error-prone.

9. **Seed data creates global departments**: 2 departments (Operations, Legal & Compliance) are seeded with `TenantId = null`, giving them no tenant affiliation. These "global" departments bypass any tenant filtering.

10. **SignalR broadcasts to ALL clients**. The dispatch pipeline broadcasts `DispatchStepCompleted` to `Clients.All` with potentially sensitive incident data (location, details). There is no per-user or per-group filtering on server-to-client messages, though `OnConnectedAsync` does auto-join department/tenant groups.

11. **Nullable navigation inconsistency**: Several entities have non-nullable FK IDs but nullable navigation properties (e.g., `PhoneTree.PhoneTreeId int` + `PhoneTree?`). Some entities do it correctly (ShiftSwap uses `= null!`). EF will silently ignore `?.` on these navigations at runtime.

### LOW SEVERITY

12. **ImportController wraps all errors in 200 OK**. Every exception is caught and returned as `ImportResult` with `IsValid = false`, using HTTP 200 instead of appropriate error codes. This masks programming errors.

13. **No server-to-client SignalR methods on hub**. The hub class only manages group membership. Actual broadcasting happens in service classes via `IHubContext<OnCallNotificationHub>`. No strongly-typed hub interface or method names are defined.

14. **Duplicate endpoints**: Phone tree GETs exist in both `DirectoryController` (`/api/directory/phone-trees`) and `PhoneTreesController` (`/api/phone-trees`). Department GETs exist in both `DepartmentsController` and `AdminController`. On-call lookups exist in both `ScheduleController` and `DirectoryController`.

15. **No test project**: No `.Tests` project exists anywhere in the solution.

16. **No FluentValidation registered for all models**. Only `ScheduleValidator` and domain validators are registered in `Program.cs` via `AddValidatorsFromAssemblyContaining<ScheduleValidator>()`. It's unclear whether FluentValidation auto-discover works for all validators in the assembly.

17. **No DELETE on SettingsController** -- settings can only be created/updated, never removed.

18. **Overlapping route namespaces**: `PhoneTreesController` and `PhoneTreeEventsController` both use `[Route("api/phone-trees")]`, which works because ASP.NET merges routes from multiple controllers, but could cause confusion.

19. **`UpdateDepartmentRequest` missing `AzureAdGroupId`**: It can be set on create but not on update (inconsistent with `CreateDepartmentRequest`).

20. **AuditLog has no FK relationships**: `TenantId`, `UserId` have no FK configuration and no navigation properties. They are loose columns.

21. **Long PK chains**: `AuditLog.Id` is `long` while all other entities use `int` or `Guid`. `AppSetting.Key` is `string` PK.

22. **Magic string enum fields**: Many fields use string values without enums or constants: `Shift.Tier`, `Shift.Status`, `PhoneTree.TreeType`, `PhoneTreeEvent.Status`, `DispatchStep.StepKey`, `DispatchStep.Status`, `TimeOff.Type`, `TimeOff.Status`, `ShiftSwap.Status`, `TenantAdmin.Role`, `Employee.Presence`.

---

## Open Questions

1. **How are employees assigned tenants in practice?** The `CreateEmployeeRequest` DTO has no `TenantId` field. `AdminService.CreateEmployeeAsync` auto-assigns the current user's tenant for sub-admins, but super admins create employees with `TenantId = null`. Is this intentional, or should super-admins pass `TenantId` explicitly?

2. **What is the expected behavior for global departments (TenantId=null)?** The seed data includes 2 global departments. Non-super-admin users won't see them. Is this deliberate?

3. **How is `EscalationService` tested?** It has no interface and no test project. The background service runs every 2 minutes with no configuration to disable this in dev (unlike sync services which check for `interval <= 0`).

4. **Is the dispatch pipeline actually reaching production systems?** The CUCM AXL client executes SQL queries directly against CUCM database via SOAP. The InformaCast and Vocera clients have hardcoded API paths. These seem like stubs or samples -- the actual queries never filter by location (CUCM `executeSQLQuery` queries all devices). The `location` parameter is used only in log messages.

5. **Where is the `TeamsBotService` used?** It's registered and injected but its usage was not traced in any controller or service path reviewed.

6. **CalendarSyncService syncs ALL unsynced shifts** (defined as shifts starting >1 day ago with any employee having AzureAdObjectId). There is no tracking of which shifts have been synced, so every cycle could re-sync the same shifts. Is this intentional?

7. **LocalAccount.Roles property**: The computed `Roles` property (`string[]` with custom getter/setter serializing to/from `RolesJson`) will conflict with EF Core. Was this intended to be ignored via `.Ignore()`, or is there a column not visible in the migration snapshot?

8. **No HATEOAS or API versioning**: All endpoints are flat with no versioning strategy. Is this acceptable for the current deployment scope?

9. **AuditLog is HIPAA-relevant but not encrypted**: PHI fields (UserName, IpAddress, Details) are stored in plaintext. Is column-level encryption planned?

10. **Consecutive days calculation in DutyHourService**: `GetConsecutiveDaysAsync` counts consecutive days by checking if shift start dates are within 1 day of each other. This doesn't account for overnight shifts (e.g., 7PM-7AM shift ending on day 2 should still count as working day 1). Is this a known limitation?