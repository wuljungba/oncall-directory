using System.Security.Claims;

namespace OnCallApi.Services;

/// <summary>
/// The one rule for "which tenants may the current caller see?".
///
/// Tenant filtering was implemented per-query and only in AdminService, so the directory,
/// schedule and code-call surfaces returned every tenant's records to anyone holding the
/// matching permission. Copying AdminService's guard into each service would have made
/// four versions of a security rule that must not drift — the same mistake that let claim
/// expansion and tenant resolution disagree about principal identity.
/// </summary>
public interface ITenantScope
{
    /// <summary>
    /// The tenants the caller may read, or null when the query should not be restricted —
    /// a super admin, or a background service running outside any request.
    /// </summary>
    Task<List<int>?> AllowedTenantIdsAsync();
}

public class TenantScope : ITenantScope
{
    private readonly ITenantContextService _tenants;
    private readonly IHttpContextAccessor _httpContextAccessor;

    public TenantScope(ITenantContextService tenants, IHttpContextAccessor httpContextAccessor)
    {
        _tenants = tenants;
        _httpContextAccessor = httpContextAccessor;
    }

    private ClaimsPrincipal? CurrentUser => _httpContextAccessor.HttpContext?.User;

    public async Task<List<int>?> AllowedTenantIdsAsync()
    {
        var user = CurrentUser;

        // No request context means a background service (AD sync, escalation, dispatch),
        // which operates on the whole estate by design.
        if (user == null || user.Identity?.IsAuthenticated != true) return null;

        if (_tenants.IsSuperAdmin(user)) return null;

        // An empty list is meaningful and must be honoured: it means "no tenants", which
        // yields no rows. Callers must not treat it as "no filter" — that was the
        // fail-open bug.
        return await _tenants.GetAuthorizedTenantIdsAsync(user);
    }
}
