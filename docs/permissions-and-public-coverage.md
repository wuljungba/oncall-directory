# Permissions, Tenant Scoping & Public Coverage — design notes

This note records two intentional behaviors of the permission-grant and public-coverage
features so future work doesn't misinterpret them.

## 1. Permission claims are global; tenancy is enforced at the admin-data layer

When a user is granted a permission — either via the per-user `PermissionGrant` table or
by being made a tenant admin (`DepartmentAdmin`) — `TenantClaimsMiddleware` expands the
`Schedule.Read/Write`, `Directory.Read/Write`, and `CodeCall.Write` claims **globally**
on their principal. There is **no per-tenant data filter** on the schedule-directory
query paths today (`ScheduleService`, `DirectoryController`).

Consequences and rationale:
- A user granted `Schedule.Write` "for tenant 1" can read/write schedule + directory data
  across tenants, exactly like an existing `DepartmentAdmin`.
- This matches the pre-existing model — `ScopedAdminPermissions` have always been
  emitted as global claims, and multi-tenant enforcement today lives in the **admin**
  data layers (`AdminService.FilterEmployeesByTenant`, `TenantContextService`), not in
  the general schedule/directory reads.
- The `TenantId` column on a `PermissionGrant` is therefore **primarily for the admin
  UI / documentation** — it does not restrict which tenant's data the grant can touch.

**Intent:** true per-tenant data filtering (scoping schedule/directory reads by the
caller's authorized tenant ids) is a deliberate **future initiative**, not a bug in the
grant feature. Doing it properly touches every schedule/shift/directory query — a
cross-cutting change that should be planned separately and rolled out carefully so it
doesn't regress existing cross-tenant behavior.

## 2. Public coverage filters by the share's tenant

`PublicScheduleController` shows only shifts whose `Department.TenantId` matches the
share's `TenantId`. Departments with a null `TenantId` (e.g. created before tenancy was
applied, or global units) are intentionally **omitted** from a public permalink. If a
subscription's coverage appears empty, the fix is to assign its departments to the
tenant (via the admin Departments tab, super-admin picker), not to change the endpoint.

## Schema note
New tables are created on existing SQL Server DBs at startup (idempotent DDL in
`Program.cs`); see that block for the exact `CREATE TABLE` statements.