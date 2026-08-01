using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OnCallApi.Authorization;
using OnCallApi.Configuration;
using OnCallApi.Data;

namespace OnCallApi.Middleware;

/// <summary>
/// Middleware that runs after authentication to expand the user's claims
/// with tenant-scoped permissions from TenantAdmin records.
///
/// For each TenantAdmin record matching the user's AzureAdObjectId,
/// this middleware adds:
///   - A "TenantId:{id}" claim with the admin's role as the value
///   - Granular permission claims (Schedule.Read, Directory.Write, etc.)
///
/// Also performs lazy auto-assignment: if the user's Azure AD group claims
/// match a Tenant.AzureAdGroupId, a TenantAdmin record is created on the fly.
/// This handles the case where a user was added to a group between sync cycles.
/// </summary>
public class TenantClaimsMiddleware
{
    private readonly RequestDelegate _next;
    private readonly SuperAdminOptions _superAdmins;

    public TenantClaimsMiddleware(RequestDelegate next, IOptions<SuperAdminOptions> superAdmins)
    {
        _next = next;
        _superAdmins = superAdmins.Value;
    }

    public async Task InvokeAsync(HttpContext context, AppDbContext db)
    {
        if (context.User.Identity?.IsAuthenticated == true)
        {
            try
            {
                var identity = context.User.Identity as ClaimsIdentity;
                if (identity == null)
                {
                    await _next(context);
                    return;
                }

                // Configured super administrators get every role + permission.
                // Real Entra/Google tokens carry no app roles, so this is the only
                // way a real user can obtain Admin.Full / Tenant.Manage today.
                if (IsConfiguredSuperAdmin(context.User))
                {
                    await GrantSuperAdminAsync(identity, db);
                }
                else
                {
                    var azureAdObjectId = GetAzureAdObjectId(context.User);
                    if (!string.IsNullOrEmpty(azureAdObjectId))
                    {
                        // Skip if tenant claims are already present (avoid re-adding on repeated requests)
                        if (!context.User.HasClaim(c => c.Type.StartsWith("TenantId:")))
                        {
                            await AddTenantClaimsAsync(identity, azureAdObjectId, db);

                            // Lazy auto-assignment via Azure AD group membership
                            await TryAutoAssignFromGroupsAsync(context.User, identity, azureAdObjectId, db);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                // If the Tenants/TenantAdmins tables don't exist yet (migration not applied)
                // or any DB error occurs, silently skip tenant claim expansion.
                // The app functions without tenant scoping until the migration is run.
                // This prevents a missing migration from breaking all API requests.
                var logger = context.RequestServices.GetRequiredService<ILogger<TenantClaimsMiddleware>>();
                logger.LogWarning(ex, "Tenant claims expansion skipped (DB not ready or tenant tables missing)");
            }
        }

        await _next(context);
    }

    private static async Task AddTenantClaimsAsync(ClaimsIdentity identity, string azureAdObjectId, AppDbContext db)
    {
        var adminRecords = await db.TenantAdmins
            .Where(a => a.AzureAdObjectId == azureAdObjectId)
            .Where(a => a.Tenant.IsActive)
            .ToListAsync();

        foreach (var admin in adminRecords)
        {
            // Add tenant-scoped claim: "TenantId:{id}" with value "DepartmentAdmin" or "SuperAdmin"
            identity.AddClaim(new Claim($"TenantId:{admin.TenantId}", admin.Role));

            // Grant scoped admin permissions if they're a DepartmentAdmin
            if (admin.Role == "DepartmentAdmin" || admin.Role == "SuperAdmin")
            {
                foreach (var perm in Permissions.ScopedAdminPermissions)
                {
                    // Only add if not already present (avoid duplicates with Admin.Full)
                    if (!identity.HasClaim(Permissions.ClaimType, perm))
                    {
                        identity.AddClaim(new Claim(Permissions.ClaimType, perm));
                    }
                }
            }
        }
    }

    private static async Task TryAutoAssignFromGroupsAsync(
        ClaimsPrincipal user,
        ClaimsIdentity identity,
        string azureAdObjectId,
        AppDbContext db)
    {
        // Get user's Azure AD group membership claims
        var userGroupIds = user.FindAll("groups")
            .Select(c => c.Value)
            .Concat(user.FindAll("groups:id")
                .Select(c => c.Value))
            .Distinct()
            .ToHashSet();

        if (userGroupIds.Count == 0)
            return;

        // Find tenants whose AzureAdGroupId matches one of the user's groups
        var matchingTenants = await db.Tenants
            .Where(t => t.IsActive && t.AzureAdGroupId != null)
            .Where(t => userGroupIds.Contains(t.AzureAdGroupId!))
            .ToListAsync();

        foreach (var tenant in matchingTenants)
        {
            // Check if already assigned
            var existing = await db.TenantAdmins
                .AnyAsync(a => a.TenantId == tenant.Id && a.AzureAdObjectId == azureAdObjectId);

            if (!existing)
            {
                db.TenantAdmins.Add(new Models.TenantAdmin
                {
                    TenantId = tenant.Id,
                    AzureAdObjectId = azureAdObjectId,
                    Role = "DepartmentAdmin",
                    IsAutoAssigned = true,
                    CreatedAt = DateTime.UtcNow,
                    LastSyncedAt = DateTime.UtcNow,
                });
                await db.SaveChangesAsync();
            }
        }
    }

    /// <summary>
    /// Whether the authenticated user matches a configured super administrator
    /// (by email or Entra object ID).
    /// </summary>
    private bool IsConfiguredSuperAdmin(ClaimsPrincipal user)
    {
        var email = GetEmail(user);
        if (!string.IsNullOrEmpty(email) &&
            _superAdmins.Emails.Contains(email, StringComparer.OrdinalIgnoreCase))
        {
            return true;
        }

        var oid = GetAzureAdObjectId(user);
        if (!string.IsNullOrEmpty(oid) &&
            _superAdmins.ObjectIds.Contains(oid, StringComparer.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    /// <summary>
    /// Grants a configured super administrator every role, every permission, and
    /// SuperAdmin status on every active tenant.
    /// </summary>
    private static async Task GrantSuperAdminAsync(ClaimsIdentity identity, AppDbContext db)
    {
        foreach (var role in Permissions.SuperAdminRoles)
        {
            if (!identity.HasClaim(ClaimTypes.Role, role))
                identity.AddClaim(new Claim(ClaimTypes.Role, role));
        }

        foreach (var perm in Permissions.SuperAdminPermissions)
        {
            if (!identity.HasClaim(Permissions.ClaimType, perm))
                identity.AddClaim(new Claim(Permissions.ClaimType, perm));
        }

        // Super admin of every active tenant, so /api/auth/me exposes the full list.
        if (!identity.Claims.Any(c => c.Type.StartsWith("TenantId:")))
        {
            try
            {
                var tenantIds = await db.Tenants.Where(t => t.IsActive).Select(t => t.Id).ToListAsync();
                foreach (var id in tenantIds)
                    identity.AddClaim(new Claim($"TenantId:{id}", "SuperAdmin"));
            }
            catch
            {
                // Tenants table missing / DB not ready — roles+permissions above still grant access.
            }
        }
    }

    private static string? GetEmail(ClaimsPrincipal user)
    {
        return user.FindFirst(ClaimTypes.Email)?.Value
            ?? user.FindFirst("email")?.Value
            ?? user.FindFirst("preferred_username")?.Value;
    }

    private static string? GetAzureAdObjectId(ClaimsPrincipal user)
    {
        return user.FindFirst("oid")?.Value
            ?? user.FindFirst(ClaimTypes.NameIdentifier)?.Value;
    }
}
