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
            if (string.IsNullOrEmpty(azureAdObjectId))
                return [];

            var tenantIds = await _db.TenantAdmins
                .Where(a => a.AzureAdObjectId == azureAdObjectId)
                .Where(a => a.Tenant.IsActive)
                .Select(a => a.TenantId)
                .Distinct()
                .ToListAsync();

            if (httpContext != null)
                httpContext.Items[TenantIdsCacheKey] = tenantIds;

            return tenantIds;
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
        var azureAdObjectId = GetAzureAdObjectId(user);
        if (string.IsNullOrEmpty(azureAdObjectId))
            return null;

        var employee = await _db.Employees
            .Where(e => e.AzureAdObjectId == azureAdObjectId)
            .Select(e => (Guid?)e.Id)
            .FirstOrDefaultAsync();

        return employee;
    }

    private static string? GetAzureAdObjectId(ClaimsPrincipal user)
    {
        // Try "oid" claim (Azure AD standard)
        var oid = user.FindFirst("oid")?.Value;
        if (!string.IsNullOrEmpty(oid)) return oid;

        // Fallback to sub claim
        return user.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
    }
}
