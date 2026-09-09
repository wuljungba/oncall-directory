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
    private readonly OnCallApi.Configuration.SuperAdminOptions _superAdmins;

    private const string TenantIdsCacheKey = "TenantContext_AuthorizedTenantIds";
    private const string TenantRoleCacheKey = "TenantContext_UserTenantRole";
    private const string IsTenantAdminCacheKey = "TenantContext_IsTenantAdmin";

    /// <summary>
    /// <paramref name="superAdmins"/> is optional so the service can still be constructed
    /// with two arguments, which a number of tests do. DI always supplies it; without it the
    /// diagnostic simply reports nobody as a configured super admin.
    /// </summary>
    public TenantContextService(
        AppDbContext db,
        IHttpContextAccessor httpContextAccessor,
        Microsoft.Extensions.Options.IOptions<OnCallApi.Configuration.SuperAdminOptions>? superAdmins = null)
    {
        _db = db;
        _httpContextAccessor = httpContextAccessor;
        _superAdmins = superAdmins?.Value ?? new OnCallApi.Configuration.SuperAdminOptions();
    }

    /// <summary>Every active tenant. The value a system-wide grant resolves to.</summary>
    private Task<List<int>> ActiveTenantIdsAsync() =>
        _db.Tenants.Where(t => t.IsActive).Select(t => t.Id).ToListAsync();

    /// <summary>
    /// Why a named principal can reach the tenants it can reach.
    ///
    /// Takes an email or object id rather than a ClaimsPrincipal, because the point is to
    /// explain somebody else's access without their token. The consequence is that the
    /// connected-directory (`tid`) rule cannot be evaluated here — it depends on a claim only
    /// their token carries — so it is reported as conditional rather than counted.
    /// </summary>
    public async Task<TenantAccessExplanation> ExplainTenantAccessAsync(string principal)
    {
        var who = (principal ?? string.Empty).Trim();
        var sources = new List<TenantAccessSource>();
        var effective = new List<int>();

        if (who.Length == 0)
            return new TenantAccessExplanation { Principal = who };

        var allActive = await ActiveTenantIdsAsync();

        // ── Configured super admin: the only legitimate source of Admin.Full ──
        var byEmail = _superAdmins.Emails.Contains(who, StringComparer.OrdinalIgnoreCase);
        var byOid = _superAdmins.ObjectIds.Contains(who, StringComparer.OrdinalIgnoreCase);
        if (byEmail || byOid)
        {
            sources.Add(new TenantAccessSource
            {
                Source = "SuperAdminConfig",
                TenantIds = allActive,
                Detail = $"Listed in Authentication:SuperAdmins:{(byEmail ? "Emails" : "ObjectIds")}. "
                       + "Holds Admin.Full, which short-circuits to every active tenant.",
            });
            effective.AddRange(allActive);
        }

        // ── TenantAdmin rows. Matched on object id, so an address never matches one. ──
        if (!who.Contains('@'))
        {
            var adminRows = await _db.TenantAdmins
                .Where(a => a.AzureAdObjectId == who && a.Tenant.IsActive)
                .Select(a => new { a.TenantId, a.Role })
                .ToListAsync();

            if (adminRows.Count > 0)
            {
                sources.Add(new TenantAccessSource
                {
                    Source = "TenantAdmin",
                    TenantIds = adminRows.Select(a => a.TenantId).Distinct().ToList(),
                    Detail = $"{adminRows.Count} TenantAdmin row(s), role(s) "
                           + $"{string.Join(", ", adminRows.Select(a => a.Role).Distinct())}. "
                           + "Confers Admin.Scoped on those tenants only.",
                });
                effective.AddRange(adminRows.Select(a => a.TenantId));
            }
        }

        // ── Permission grants. A null TenantId is the widest grant there is. ──
        var grants = await _db.PermissionGrants
            .Where(g => g.IsActive && g.ExternalPrincipalId == who)
            .Select(g => new { g.Id, g.TenantId, g.Permissions })
            .ToListAsync();

        foreach (var g in grants)
        {
            if (g.TenantId == null)
            {
                sources.Add(new TenantAccessSource
                {
                    Source = "PermissionGrant",
                    TenantIds = allActive,
                    Detail = $"Grant #{g.Id} has NO TenantId — a system-wide grant, which resolves to "
                           + $"every active tenant. Permissions: {g.Permissions}.",
                });
                effective.AddRange(allActive);
            }
            else if (allActive.Contains(g.TenantId.Value))
            {
                sources.Add(new TenantAccessSource
                {
                    Source = "PermissionGrant",
                    TenantIds = [g.TenantId.Value],
                    Detail = $"Grant #{g.Id}, scoped to tenant {g.TenantId.Value}. Permissions: {g.Permissions}.",
                });
                effective.Add(g.TenantId.Value);
            }
            else
            {
                sources.Add(new TenantAccessSource
                {
                    Source = "PermissionGrant",
                    TenantIds = [],
                    Detail = $"Grant #{g.Id} points at tenant {g.TenantId.Value}, which is not active — contributes nothing.",
                });
            }
        }

        // ── Connected directory. Cannot be settled without their token. ──
        var connected = await _db.Tenants
            .Where(t => t.IsActive && t.AzureAdTenantId != null && t.AzureAdTenantId != "")
            .Select(t => new { t.Id, t.Name, t.AzureAdTenantId })
            .ToListAsync();

        if (connected.Count > 0)
        {
            sources.Add(new TenantAccessSource
            {
                Source = "ConnectedDirectory",
                Conditional = true,
                TenantIds = connected.Select(c => c.Id).ToList(),
                Detail = "Applies only if this principal's token carries a tid matching one of: "
                       + string.Join(", ", connected.Select(c => $"{c.Name}={c.AzureAdTenantId}"))
                       + ". Not determinable without their token; a Google or local account carries no tid.",
            });
        }

        return new TenantAccessExplanation
        {
            Principal = who,
            IsConfiguredSuperAdmin = byEmail || byOid,
            EffectiveTenantIds = effective.Distinct().OrderBy(id => id).ToList(),
            Sources = sources,
        };
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
            var tid = OnCallApi.Authorization.PrincipalClaims.GetTenantId(user);

            // A connected-directory user may have neither of the two identifiers below —
            // their access comes from which directory issued the token, not from a row
            // keyed to them — so the early exit has to account for `tid` as well or the
            // claim would say "you are in tenant 3" while every query filtered tenant 3 out.
            if (string.IsNullOrEmpty(azureAdObjectId)
                && string.IsNullOrEmpty(email)
                && string.IsNullOrEmpty(tid))
                return [];

            var tenantIds = new List<int>();

            // Tenants whose directory this token came from. Mirrors
            // TenantClaimsMiddleware.TryScopeFromConnectedTenantAsync; the two must agree,
            // because that one decides what a user may do and this one decides what they
            // may see.
            if (!string.IsNullOrEmpty(tid)
                && !string.Equals(tid, "common", StringComparison.OrdinalIgnoreCase))
            {
                tenantIds.AddRange(await _db.Tenants
                    .Where(t => t.IsActive && t.AzureAdTenantId == tid)
                    .Select(t => t.Id)
                    .ToListAsync());
            }

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

            // There is one tenant-admin role. The two that preceded it ranked against
            // each other here, but conferred identical permissions, so the ranking decided
            // nothing -- any recognised row means tenant admin.
            var roles = await _db.TenantAdmins
                .Where(a => a.AzureAdObjectId == azureAdObjectId)
                .Select(a => a.Role)
                .Distinct()
                .ToListAsync();

            var highestRole = roles.Any(OnCallApi.Authorization.TenantAdminRoles.IsTenantAdmin)
                ? OnCallApi.Authorization.TenantAdminRoles.Default
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

    public async Task<int?> GetDepartmentTenantIdAsync(int departmentId)
    {
        try
        {
            return await _db.Departments
                .Where(d => d.Id == departmentId)
                .Select(d => d.TenantId)
                .FirstOrDefaultAsync();
        }
        catch
        {
            return null;
        }
    }

    // Both resolvers delegate to PrincipalClaims so that claim expansion and tenant
    // resolution can never disagree about who a principal is.
    private static string? GetEmail(ClaimsPrincipal user) => PrincipalClaims.GetEmail(user);

    private static string? GetAzureAdObjectId(ClaimsPrincipal user) => PrincipalClaims.GetObjectId(user);
}
