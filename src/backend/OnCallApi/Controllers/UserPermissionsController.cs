using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OnCallApi.Authorization;
using OnCallApi.Data;
using OnCallApi.Models;
using OnCallApi.Services;

namespace OnCallApi.Controllers;

/// <summary>
/// Per-user permission grants for the on-call schedule (and directory). Lets an
/// administrator assign granular read/write permissions (Schedule.Read,
/// Schedule.Write, Directory.Read, Directory.Write, …) to a specific user —
/// including external principals whose Entra/Google tokens carry no app roles.
/// Grants are honored by <c>TenantClaimsMiddleware</c>.
/// </summary>
[ApiController]
[Route("api/admin/permissions")]
[Authorize(Policy = "RequireAdminFullOrTenantManage")]
public class UserPermissionsController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly ITenantContextService _tenants;

    public UserPermissionsController(AppDbContext db, ITenantContextService tenants)
    {
        _db = db;
        _tenants = tenants;
    }

    /// <summary>List permission grants. Super admins see all; scoped admins see their tenants' only.</summary>
    [HttpGet]
    public async Task<ActionResult<List<PermissionGrantResponse>>> List([FromQuery] int? tenantId = null)
    {
        var query = _db.PermissionGrants.AsNoTracking();

        if (_tenants.IsSuperAdmin(User))
        {
            if (tenantId.HasValue)
            {
                query = query.Where(g => g.TenantId == tenantId.Value);
            }
        }
        else
        {
            var tenantIds = await _tenants.GetAuthorizedTenantIdsAsync(User);
            query = query.Where(g => !g.TenantId.HasValue || tenantIds.Contains(g.TenantId.Value));
            if (tenantId.HasValue)
            {
                query = query.Where(g => g.TenantId == tenantId.Value);
            }
        }

        var grants = await query.OrderBy(g => g.ExternalPrincipalId).ToListAsync();
        return Ok(grants.Select(ToResponse).ToList());
    }

    /// <summary>Grant a permission set to a user.</summary>
    [HttpPost]
    public async Task<ActionResult<PermissionGrantResponse>> Create(CreatePermissionGrantRequest request)
    {
        var perms = Permissions.ParseAssignablePermissionCsv(request.Permissions);
        if (perms.Length == 0)
        {
            return BadRequest(new
            {
                error = "At least one valid assignable permission is required (Schedule.Read, Schedule.Write, Directory.Read, Directory.Write, CodeCall.Write)."
            });
        }

        if (string.IsNullOrWhiteSpace(request.ExternalPrincipalId))
        {
            return BadRequest(new { error = "A principal identifier (Entra object id or email) is required." });
        }

        if (request.TenantId.HasValue && !await CanManageTenantAsync(request.TenantId.Value))
            return Forbid();
        if (!request.TenantId.HasValue && !_tenants.IsSuperAdmin(User))
            return Forbid();

        var grant = new PermissionGrant
        {
            TenantId = request.TenantId,
            PrincipalType = string.IsNullOrWhiteSpace(request.PrincipalType) ? "external" : request.PrincipalType!,
            ExternalPrincipalId = request.ExternalPrincipalId.Trim(),
            Permissions = string.Join(",", perms),
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
        };

        _db.PermissionGrants.Add(grant);
        await _db.SaveChangesAsync();
        return Ok(ToResponse(grant));
    }

    /// <summary>Update a grant (change permissions, revoke, re-activate).</summary>
    [HttpPut("{id}")]
    public async Task<ActionResult<PermissionGrantResponse>> Update(int id, UpdatePermissionGrantRequest request)
    {
        var grant = await _db.PermissionGrants.FindAsync(id);
        if (grant == null) return NotFound();

        if (grant.TenantId.HasValue && !await CanManageTenantAsync(grant.TenantId.Value))
            return Forbid();
        if (!grant.TenantId.HasValue && !_tenants.IsSuperAdmin(User))
            return Forbid();

        if (request.Permissions != null)
        {
            var perms = Permissions.ParseAssignablePermissionCsv(request.Permissions);
            if (perms.Length == 0)
            {
                return BadRequest(new { error = "At least one valid assignable permission is required." });
            }
            grant.Permissions = string.Join(",", perms);
        }

        if (request.IsActive.HasValue) grant.IsActive = request.IsActive.Value;
        if (!string.IsNullOrWhiteSpace(request.ExternalPrincipalId)) grant.ExternalPrincipalId = request.ExternalPrincipalId.Trim();
        if (!string.IsNullOrWhiteSpace(request.PrincipalType)) grant.PrincipalType = request.PrincipalType!;

        grant.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return Ok(ToResponse(grant));
    }

    /// <summary>Revoke (hard-delete) a grant.</summary>
    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(int id)
    {
        var grant = await _db.PermissionGrants.FindAsync(id);
        if (grant == null) return NotFound();
        if (grant.TenantId.HasValue && !await CanManageTenantAsync(grant.TenantId.Value))
            return Forbid();
        if (!grant.TenantId.HasValue && !_tenants.IsSuperAdmin(User))
            return Forbid();

        _db.PermissionGrants.Remove(grant);
        await _db.SaveChangesAsync();
        return NoContent();
    }

    private async Task<bool> CanManageTenantAsync(int tenantId)
    {
        if (_tenants.IsSuperAdmin(User)) return true;
        var ids = await _tenants.GetAuthorizedTenantIdsAsync(User);
        return ids.Contains(tenantId);
    }

    private static PermissionGrantResponse ToResponse(PermissionGrant g) => new()
    {
        Id = g.Id,
        TenantId = g.TenantId,
        PrincipalType = g.PrincipalType,
        ExternalPrincipalId = g.ExternalPrincipalId,
        Permissions = Permissions.ParsePermissionCsv(g.Permissions),
        IsActive = g.IsActive,
        CreatedAt = g.CreatedAt,
        UpdatedAt = g.UpdatedAt,
    };
}

public record CreatePermissionGrantRequest(int? TenantId, string? PrincipalType, string ExternalPrincipalId, string Permissions);
public record UpdatePermissionGrantRequest(string? ExternalPrincipalId, string? PrincipalType, string? Permissions, bool? IsActive);

public class PermissionGrantResponse
{
    public int Id { get; set; }
    public int? TenantId { get; set; }
    public string PrincipalType { get; set; } = "external";
    public string ExternalPrincipalId { get; set; } = string.Empty;
    public string[] Permissions { get; set; } = [];
    public bool IsActive { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
}