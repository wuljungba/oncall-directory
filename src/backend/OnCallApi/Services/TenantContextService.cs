using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using OnCallApi.Data;
using OnCallApi.Authorization;

namespace OnCallApi.Services;

/// <summary>
/// Resolves tenant context from the database for the current user.
/// Caches results per HTTP request using HttpContext.Items to avoid repeated DB lookups.
/// </summary>
public class TenantContextService : ITenantContextService
{
    private readonly AppDbContext _db;
    private readonly IHttpContextAccessor _httpContextAccessor;

    private const string TenantIdsCacheKey = "TenantContext_AuthorizedTenantIds";
    private const string TenantRoleCacheKey = "TenantContext_UserTenantRole";
    private const string IsTenantAdminCacheKey = "TenantContext_IsTenantAdmin";

    public TenantContextService(AppDbContext db, IHttpContextAccessor httpContextAccessor)
    {
        _db = db;
        _httpContextAccessor = httpContextAccessor;
    }

    public bool IsSuperAdmin(ClaimsPrincipal user)
    {
        return user.HasClaim(Permissions.ClaimType, Permissions.AdminFull);
    }

    public async Task<List<int>> GetAuthorizedTenantIdsAsync(ClaimsPrincipal user)
    {
        try
        {
            // Super admins see all tenants
            if (IsSuperAdmin(user))
            {
                return await _db.Tenants
                    .Where(t => t.IsActive)
                    .Select(t => t.Id)
                    .ToListAsync();
            }

            // Check cache
            var httpContext = _httpContextAccessor.HttpContext;
            if (httpContext?.Items[TenantIdsCacheKey] is List<int> cached)
                return cached;

            var azureAdObjectId = GetAzureAdObjectId(user);
            var email = GetEmail(user);
            if (string.IsNullOrEmpty(azureAdObjectId) && string.IsNullOrEmpty(email))
                return [];

            var tenantIds = new List<int>();

            if (!string.IsNullOrEmpty(azureAdObjectId))
            {
                tenantIds.AddRange(await _db.TenantAdmins
                    .Where(a => a.AzureAdObjectId == azureAdObjectId)
                    .Where(a => a.Tenant.IsActive)
                    .Select(a => a.TenantId)
                    .ToListAsync());
            }

            // An explicit permission grant is also tenant membership for the purposes of
            // reading data. Without this, a granted user resolves to no tenants at all —
            // which under fail-closed scoping means they can see nothing, and previously
            // (fail-open) meant they could see everything.
            var grants = await _db.PermissionGrants
                .Where(g => g.IsActive)
                .Where(g => (azureAdObjectId != null && g.ExternalPrincipalId == azureAdObjectId)
                         || (email != null && g.ExternalPrincipalId == email))
                .Select(g => g.TenantId)
                .ToListAsync();

            // A system-wide grant (TenantId == null) is exactly what a super admin chose
            // when they picked "All tenants", so it resolves to every active tenant.
            if (grants.Any(t => !t.HasValue))
            {
                tenantIds.AddRange(await _db.Tenants
                    .Where(t => t.IsActive)
                    .Select(t => t.Id)
                    .ToListAsync());
            }
            else
            {
                // Only active tenants count, matching the TenantAdmin lookup above:
                // deactivating a subscription must actually withdraw access to its data.
                var granted = grants.Where(t => t.HasValue).Select(t => t!.Value).ToList();
                if (granted.Count > 0)
                {
                    tenantIds.AddRange(await _db.Tenants
                        .Where(t => t.IsActive && granted.Contains(t.Id))
                        .Select(t => t.Id)
                        .ToListAsync());
                }
            }

            var distinct = tenantIds.Distinct().ToList();

            if (httpContext != null)
                httpContext.Items[TenantIdsCacheKey] = distinct;

            return distinct;
        }
        catch
        {
            // If Tenants/TenantAdmins tables don't exist, return empty (no tenant access).
            // This allows the app to function normally until the migration is applied.
            return [];
        }
    }

    public async Task<string?> GetUserTenantRoleAsync(ClaimsPrincipal user)
    {
        try
        {
            // Check cache
            var httpContext = _httpContextAccessor.HttpContext;
            if (httpContext?.Items[TenantRoleCacheKey] is string cached)
                return cached;

            var azureAdObjectId = GetAzureAdObjectId(user);
            if (string.IsNullOrEmpty(azureAdObjectId))
                return null;

            // Get the highest role (SuperAdmin > DepartmentAdmin)
            var roles = await _db.TenantAdmins
                .Where(a => a.AzureAdObjectId == azureAdObjectId)
                .Select(a => a.Role)
                .Distinct()
                .ToListAsync();

            var highestRole = roles.Contains("SuperAdmin") ? "SuperAdmin"
                            : roles.Contains("DepartmentAdmin") ? "DepartmentAdmin"
                            : null;

            if (httpContext != null)
                httpContext.Items[TenantRoleCacheKey] = highestRole;

            return highestRole;
        }
        catch { return null; }
    }

    public async Task<bool> IsTenantAdminAsync(ClaimsPrincipal user)
    {
        try
        {
            // Check cache
            var httpContext = _httpContextAccessor.HttpContext;
            if (httpContext?.Items[IsTenantAdminCacheKey] is bool cached)
                return cached;

            if (IsSuperAdmin(user))
            {
                if (httpContext != null)
                    httpContext.Items[IsTenantAdminCacheKey] = true;
                return true;
            }

            var azureAdObjectId = GetAzureAdObjectId(user);
            if (string.IsNullOrEmpty(azureAdObjectId))
                return false;

            var isAdmin = await _db.TenantAdmins
                .AnyAsync(a => a.AzureAdObjectId == azureAdObjectId);

            if (httpContext != null)
                httpContext.Items[IsTenantAdminCacheKey] = isAdmin;

            return isAdmin;
        }
        catch { return false; }
    }

    public async Task<Guid?> GetCurrentEmployeeIdAsync(ClaimsPrincipal user)
    {
        // Local accounts carry an explicit "employee_id" claim = the internal Employee.Id.
        if (Guid.TryParse(user.FindFirst("employee_id")?.Value, out var direct))
            return direct;

        var azureAdObjectId = GetAzureAdObjectId(user);
        if (string.IsNullOrEmpty(azureAdObjectId))
            return null;

        var employee = await _db.Employees
            .Where(e => e.AzureAdObjectId == azureAdObjectId)
            .Select(e => (Guid?)e.Id)
            .FirstOrDefaultAsync();

        return employee;
    }

    // Both resolvers delegate to PrincipalClaims so that claim expansion and tenant
    // resolution can never disagree about who a principal is.
    private static string? GetEmail(ClaimsPrincipal user) => PrincipalClaims.GetEmail(user);

    private static string? GetAzureAdObjectId(ClaimsPrincipal user) => PrincipalClaims.GetObjectId(user);
}
