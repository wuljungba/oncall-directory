# Admin Access Management

This document describes how to grant users admin access to specific tenants in the OnCall system.

## Overview

The system uses **tenant-scoped admin roles** to give users administrative permissions limited to specific tenants (hospitals, facilities, business units). This prevents accidental misconfiguration of unrelated tenants and limits the blast radius of compromised credentials.

Admin roles:
- **SuperAdmin** — Full control over a tenant: create/edit schedules, departments, users, escalation rules, integrations
- **DepartmentAdmin** — Limited to managing departments within a tenant (cannot manage tenant-level settings)

## For Current Users

### Grant Admin Access via SQL

To grant a user admin access to a specific tenant:

1. **Find the user's Azure AD Object ID**
   - If using Microsoft Entra: Azure Portal → Users → Find user → copy "Object ID"
   - If using Google: Get their email address (used as identifier)
   - If using local account: Use their email address

2. **Find the tenant's database ID**
   ```sql
   SELECT Id, Name FROM Tenants WHERE Name = 'Your Tenant Name';
   ```

3. **Insert a TenantAdmin record**
   ```sql
   INSERT INTO TenantAdmins (TenantId, AzureAdObjectId, Role, IsAutoAssigned, CreatedAt, LastSyncedAt)
   VALUES (
       <TENANT_ID>,                    -- From step 2
       '<AZURE_AD_OBJECT_ID>',         -- From step 1
       'SuperAdmin',                   -- Or 'DepartmentAdmin' for limited access
       0,                              -- IsAutoAssigned (0 = manual, 1 = via Azure AD group)
       GETUTCDATE(),
       GETUTCDATE()
   );
   ```

### Grant Admin Access via Admin UI (if you have admin access)

1. Log in as an existing admin
2. Navigate to **Admin → Users & Permissions**
3. Click **+ Add User**
4. Enter user's email/object ID
5. Select **Tenant** and **Role** (SuperAdmin/DepartmentAdmin)
6. Click **Grant Access**

## For Future Users

### Automatic via Azure AD Group Mapping

Set up automatic admin assignment when users join an Azure AD group:

1. **In OnCall Admin Dashboard:**
   - Go to **Admin → Tenants**
   - Select a tenant → **Edit**
   - Under **Azure AD Group ID**, enter your group's object ID
   
2. **In Azure AD:**
   - Add users to that group
   
3. **On first sign-in:**
   - The system automatically creates a `TenantAdmin` record
   - User is granted DepartmentAdmin permissions

This is the **recommended approach** for production — it's:
- Automatic (no manual SQL)
- Auditable (visible in database as `IsAutoAssigned = 1`)
- Reversible (removing from group removes access)

### Manual SQL (for testing or one-off grants)

Use the SQL script in `scripts/create-dev-admin.sql` as a template:

```sql
-- Template for granting a new user admin access
DECLARE @TenantId INT = <TENANT_ID>;
DECLARE @ObjectId NVARCHAR(MAX) = '<USER_OBJECT_ID_OR_EMAIL>';

INSERT INTO TenantAdmins (TenantId, AzureAdObjectId, Role, IsAutoAssigned, CreatedAt, LastSyncedAt)
VALUES (@TenantId, @ObjectId, 'SuperAdmin', 0, GETUTCDATE(), GETUTCDATE());
```

## Revoking Admin Access

### Remove via SQL

```sql
DELETE FROM TenantAdmins
WHERE TenantId = <TENANT_ID>
AND AzureAdObjectId = '<USER_OBJECT_ID>';
```

### Remove via Azure AD Group (if using auto-assignment)

1. In Azure AD: Remove user from the group
2. System automatically revokes `DepartmentAdmin` permissions on next request
3. (TenantAdmin record remains for audit, but permissions are not applied)

### Remove via Admin UI

1. **Admin → Users & Permissions**
2. Find the user
3. Click **Revoke Access**

## Important Notes

### Dev User Setup

For local development with dev auth (`DevAuth:Enabled=true`):
- Dev user has object ID: `00000000-0000-0000-0000-000000000001`
- To grant dev admin to Test tenant:
  ```sql
  INSERT INTO TenantAdmins (TenantId, AzureAdObjectId, Role, IsAutoAssigned, CreatedAt, LastSyncedAt)
  VALUES (<TEST_TENANT_ID>, '00000000-0000-0000-0000-000000000001', 'SuperAdmin', 0, GETUTCDATE(), GETUTCDATE());
  ```

### Authorization Flow

When a user signs in:

1. **Authentication** → User's identity and claims are verified
2. **TenantClaimsMiddleware** (backend) → Queries `TenantAdmins` table for matching records
3. **Claims Added** → User gets `TenantId:{id}` claim for each tenant they admin
4. **Authorization** → Controllers check claims to enforce tenant scoping
5. **Frontend** → Shows only tenants the user has admin access to

If a user has no TenantAdmin records, they have **no admin access to any tenant**.

### Audit Trail

Every TenantAdmin record has:
- `CreatedAt` — When access was granted
- `LastSyncedAt` — When it was last validated (for auto-assigned records)
- `IsAutoAssigned` — Whether it came from Azure AD group membership (1) or manual assignment (0)

Check the audit log to see who has access to what:

```sql
SELECT 
    ta.TenantId,
    t.Name AS TenantName,
    ta.AzureAdObjectId,
    ta.Role,
    CASE WHEN ta.IsAutoAssigned = 1 THEN 'Auto (Azure AD Group)' ELSE 'Manual' END AS Source,
    ta.CreatedAt,
    ta.LastSyncedAt
FROM TenantAdmins ta
JOIN Tenants t ON ta.TenantId = t.Id
ORDER BY ta.TenantId, ta.CreatedAt DESC;
```

## Troubleshooting

### User has no admin access after signing in

**Check:** Run the audit query above. Verify a `TenantAdmin` record exists for that user and tenant.

**Fix:** Insert the record using SQL above.

### User sees all tenants instead of just their assigned tenant

**Check:** Backend's authorization middleware is not scoping correctly.

**Verify:** 
- User's TenantAdmin record exists
- Backend has restarted since the record was created
- No misconfiguration in `Authorization:SuperAdmins` (should be empty unless user is a global super admin)

### User was removed from Azure AD group but still has access

**Check:** Auto-assigned TenantAdmin record still exists in database.

**Fix:** Either:
1. Delete the record manually: `DELETE FROM TenantAdmins WHERE ... AND IsAutoAssigned = 1`
2. Or rely on the next sync to clean it up (automatic on next sign-in if configured)

## Best Practices

1. **Use Azure AD groups for production** — automates access, auditable, reversible
2. **Use manual SQL only for testing** — dev/staging environments
3. **Grant SuperAdmin only when necessary** — DepartmentAdmin limits blast radius
4. **Review access quarterly** — run the audit query above to catch stale records
5. **Document why each person has access** — add notes to ticket/PR when granting

## Related

- `scripts/create-dev-admin.sql` — Template SQL for dev environment
- `src/backend/OnCallApi/Middleware/TenantClaimsMiddleware.cs` — How claims are expanded
- `src/backend/OnCallApi/Authorization/Permissions.cs` — Permission definitions
