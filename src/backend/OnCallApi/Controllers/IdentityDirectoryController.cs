using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using OnCallApi.Authorization;
using OnCallApi.Configuration;
using OnCallApi.Data;
using OnCallApi.Services;

namespace OnCallApi.Controllers;

/// <summary>
/// Who has signed in, and what access they currently hold.
///
/// Entra and Google tokens carry no app roles, so a new user arrives with no permissions
/// and nothing to identify them by. This endpoint answers "who showed up?" so an admin can
/// grant access from a list instead of having to know someone's exact email address, and
/// exposes the object id that <see cref="TenantAdminsController"/> needs to appoint a
/// sub-admin.
///
/// Read-only: nothing here changes access.
/// </summary>
[ApiController]
[Route("api/admin/identities")]
[Authorize(Policy = "RequireAdminFullOrScoped")]
public class IdentityDirectoryController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly ITenantContextService _tenants;
    private readonly SuperAdminOptions _superAdmins;

    public IdentityDirectoryController(
        AppDbContext db, ITenantContextService tenants, IOptions<SuperAdminOptions> superAdmins)
    {
        _db = db;
        _tenants = tenants;
        _superAdmins = superAdmins.Value;
    }

    [HttpGet]
    public async Task<ActionResult<List<SignInIdentityResponse>>> List([FromQuery] int take = 200)
    {
        take = Math.Clamp(take, 1, 500);

        var identities = await _db.SignInIdentities
            .AsNoTracking()
            .OrderByDescending(i => i.LastSeenAt)
            .Take(take)
            .ToListAsync();

        var grants = await _db.PermissionGrants.AsNoTracking().Where(g => g.IsActive).ToListAsync();
        var tenantAdmins = await _db.TenantAdmins.AsNoTracking().ToListAsync();

        var isSuperAdmin = _tenants.IsSuperAdmin(User);
        var visibleTenants = isSuperAdmin ? null : await _tenants.GetAuthorizedTenantIdsAsync(User);

        var results = new List<SignInIdentityResponse>();

        foreach (var identity in identities)
        {
            // Mirrors TenantClaimsMiddleware.AddPermissionGrantsAsync: a grant matches on
            // object id OR email, so the same person is recognised either way.
            var matched = grants.Where(g =>
                    string.Equals(g.ExternalPrincipalId, identity.ExternalObjectId, StringComparison.OrdinalIgnoreCase)
                    || (identity.Email != null &&
                        string.Equals(g.ExternalPrincipalId, identity.Email, StringComparison.OrdinalIgnoreCase)))
                .ToList();

            var adminRows = tenantAdmins
                .Where(a => string.Equals(a.AzureAdObjectId, identity.ExternalObjectId, StringComparison.OrdinalIgnoreCase))
                .ToList();

            var isConfiguredSuperAdmin =
                (identity.Email != null && _superAdmins.Emails.Contains(identity.Email, StringComparer.OrdinalIgnoreCase))
                || _superAdmins.ObjectIds.Contains(identity.ExternalObjectId, StringComparer.OrdinalIgnoreCase);

            // A scoped admin sees the people they can actually act on: those inside their
            // tenants, plus anyone with no access at all (newcomers needing provisioning).
            if (!isSuperAdmin)
            {
                var hasAnyAccess = matched.Count > 0 || adminRows.Count > 0 || isConfiguredSuperAdmin;
                var inMyTenants =
                    matched.Any(g => g.TenantId.HasValue && visibleTenants!.Contains(g.TenantId.Value))
                    || adminRows.Any(a => visibleTenants!.Contains(a.TenantId));

                if (hasAnyAccess && !inMyTenants) continue;
            }

            results.Add(new SignInIdentityResponse
            {
                Id = identity.Id,
                Provider = identity.Provider,
                ExternalObjectId = identity.ExternalObjectId,
                Email = identity.Email,
                DisplayName = identity.DisplayName,
                FirstSeenAt = identity.FirstSeenAt,
                LastSeenAt = identity.LastSeenAt,
                IsSuperAdmin = isConfiguredSuperAdmin,
                TenantAdminOf = adminRows.Select(a => a.TenantId).Distinct().ToList(),
                Permissions = matched
                    .SelectMany(g => Permissions.ParsePermissionCsv(g.Permissions))
                    .Distinct()
                    .OrderBy(p => p)
                    .ToList(),
                GrantTenantIds = matched.Select(g => g.TenantId).Distinct().ToList(),
            });
        }

        return Ok(results);
    }
}

public class SignInIdentityResponse
{
    public int Id { get; set; }
    public string Provider { get; set; } = string.Empty;
    public string ExternalObjectId { get; set; } = string.Empty;
    public string? Email { get; set; }
    public string? DisplayName { get; set; }
    public DateTime FirstSeenAt { get; set; }
    public DateTime LastSeenAt { get; set; }

    /// <summary>Granted via super-admin configuration rather than any database record.</summary>
    public bool IsSuperAdmin { get; set; }

    public List<int> TenantAdminOf { get; set; } = [];
    public List<string> Permissions { get; set; } = [];

    /// <summary>Tenants their grants apply to; a null entry means a system-wide grant.</summary>
    public List<int?> GrantTenantIds { get; set; } = [];

    /// <summary>True when nothing anywhere gives this person access yet.</summary>
    public bool HasNoAccess => !IsSuperAdmin && TenantAdminOf.Count == 0 && Permissions.Count == 0;
}
