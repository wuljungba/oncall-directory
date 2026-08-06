using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OnCallApi.Data;
using OnCallApi.Models;
using OnCallApi.Services;

namespace OnCallApi.Controllers;

/// <summary>
/// Admin CRUD for public on-call permalink shares (<see cref="PublicShare"/>).
/// Each share yields a coverage-only public URL (/on-call/{token}) that admins
/// can create, copy, and revoke. Scope is tenant-bounded for sub-admins.
/// </summary>
[ApiController]
[Route("api/admin/shares")]
[Authorize(Policy = "RequireAdminFullOrTenantManage")]
public class SharedSchedulesController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly ITenantContextService _tenants;

    public SharedSchedulesController(AppDbContext db, ITenantContextService tenants)
    {
        _db = db;
        _tenants = tenants;
    }

    /// <summary>List public shares the caller may manage.</summary>
    [HttpGet]
    public async Task<ActionResult<List<PublicShareResponse>>> List()
    {
        var query = _db.PublicShares.AsNoTracking();

        if (_tenants.IsSuperAdmin(User))
        {
            var allShares = await query.Include(s => s.Tenant).ToListAsync();
            return Ok(allShares.Select(ToResponse).ToList());
        }

        var tenantIds = await _tenants.GetAuthorizedTenantIdsAsync(User);
        var shares = await query.Where(s => tenantIds.Contains(s.TenantId)).Include(s => s.Tenant).ToListAsync();
        return Ok(shares.Select(ToResponse).ToList());
    }

    /// <summary>Create a new public permalink share for a tenant.</summary>
    [HttpPost]
    public async Task<ActionResult<PublicShareResponse>> Create(CreatePublicShareRequest request)
    {
        if (request.TenantId == 0)
            return BadRequest(new { error = "A valid tenant is required." });
        if (_tenants.IsSuperAdmin(User) == false && !(await _tenants.GetAuthorizedTenantIdsAsync(User)).Contains(request.TenantId))
            return Forbid();

        var share = new PublicShare
        {
            TenantId = request.TenantId,
            Label = request.Label ?? string.Empty,
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
        };
        _db.PublicShares.Add(share);
        await _db.SaveChangesAsync();
        return Ok(ToResponse(share));
    }

    /// <summary>Revoke (delete) a public share — the permalink stops resolving.</summary>
    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(int id)
    {
        var share = await _db.PublicShares.FindAsync(id);
        if (share == null) return NotFound();
        if (_tenants.IsSuperAdmin(User) == false && !(await _tenants.GetAuthorizedTenantIdsAsync(User)).Contains(share.TenantId))
            return Forbid();

        _db.PublicShares.Remove(share);
        await _db.SaveChangesAsync();
        return NoContent();
    }

    /// <summary>Toggle active (soft-revoke without deleting).</summary>
    [HttpPut("{id}/active")]
    public async Task<ActionResult<PublicShareResponse>> SetActive(int id, SetActiveRequest request)
    {
        var share = await _db.PublicShares.FindAsync(id);
        if (share == null) return NotFound();
        if (_tenants.IsSuperAdmin(User) == false && !(await _tenants.GetAuthorizedTenantIdsAsync(User)).Contains(share.TenantId))
            return Forbid();

        share.IsActive = request.IsActive;
        await _db.SaveChangesAsync();
        return Ok(ToResponse(share));
    }

    private static PublicShareResponse ToResponse(PublicShare s) => new()
    {
        Id = s.Id,
        TenantId = s.TenantId,
        Tenant = s.Tenant?.Name ?? string.Empty,
        Token = s.Token,
        Label = s.Label,
        IsActive = s.IsActive,
        CreatedAt = s.CreatedAt,
        Permalink = $"/on-call/{s.Token}",
    };
}

public record CreatePublicShareRequest(int TenantId, string? Label);
public record SetActiveRequest(bool IsActive);

public class PublicShareResponse
{
    public int Id { get; set; }
    public int TenantId { get; set; }
    public string Tenant { get; set; } = string.Empty;
    public Guid Token { get; set; }
    public string Label { get; set; } = string.Empty;
    public bool IsActive { get; set; }
    public DateTime CreatedAt { get; set; }
    public string Permalink { get; set; } = string.Empty;
}