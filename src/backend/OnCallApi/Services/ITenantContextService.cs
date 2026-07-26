using System.Security.Claims;

namespace OnCallApi.Services;

/// <summary>
/// Provides tenant context for the current request.
/// Determines which tenants a user can access and their role within each tenant.
/// </summary>
public interface ITenantContextService
{
    /// <summary>Returns the tenant IDs the current user is authorized to manage.</summary>
    Task<List<int>> GetAuthorizedTenantIdsAsync(ClaimsPrincipal user);

    /// <summary>Returns the highest role for the user across all tenants, or null if none.</summary>
    Task<string?> GetUserTenantRoleAsync(ClaimsPrincipal user);

    /// <summary>Returns true if the user is a super admin (has Admin.Full permission).</summary>
    bool IsSuperAdmin(ClaimsPrincipal user);

    /// <summary>Returns true if the user has scoped admin access to any tenant.</summary>
    Task<bool> IsTenantAdminAsync(ClaimsPrincipal user);

    /// <summary>
    /// Returns the Employee ID for the current user based on their AzureAdObjectId.
    /// This is used to auto-assign TenantId when an employee is created by a sub-admin.
    /// </summary>
    Task<Guid?> GetCurrentEmployeeIdAsync(ClaimsPrincipal user);
}
