using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OnCallApi.Data;
using OnCallApi.Models;
using OnCallApi.Services;

namespace OnCallApi.Controllers;

[ApiController]
[Route("api/code-call-locations")]
[Authorize(Policy = "RequireDirectoryRead")]
public class CodeCallLocationsController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly ITenantContextService _tenantContext;

    public CodeCallLocationsController(AppDbContext db, ITenantContextService tenantContext)
    {
        _db = db;
        _tenantContext = tenantContext;
    }

    /// <summary>
    /// Restricts a location query to the caller's tenants.
    ///
    /// Shared by every endpoint deliberately: the filter lived only in GetAll, so a single
    /// id could read, rewrite or retire any other tenant's code-call location.
    /// </summary>
    private async Task<IQueryable<CodeCallLocation>> ScopedAsync(IQueryable<CodeCallLocation> query)
    {
        if (_tenantContext.IsSuperAdmin(User)) return query;

        var tenantIds = await _tenantContext.GetAuthorizedTenantIdsAsync(User);

        // A location with no department used to be exempt from the filter. That read as a
        // narrow edge case but was in fact the common one -- DepartmentId is nullable and
        // defaults to null, so every unassigned location was visible to, and editable by,
        // every tenant. Unassigned locations now belong to nobody but a super admin, which
        // is the fail-closed reading.
        return query.Where(l => l.Department != null
            && l.Department.TenantId.HasValue
            && tenantIds.Contains(l.Department.TenantId.Value));
    }

    /// <summary>Confirms a department the caller may file a location under.</summary>
    private async Task<bool> DepartmentAllowedAsync(int? departmentId)
    {
        // A department is required for anyone but a super admin: a location with none is
        // invisible to every scoped read above, so creating one would file a record its
        // author could never see again.
        if (departmentId == null) return _tenantContext.IsSuperAdmin(User);

        // Existence is checked for everyone, super admins included. Skipping it for them
        // meant a typo'd department id reached SaveChanges and came back as an opaque 500
        // instead of a 404.
        if (_tenantContext.IsSuperAdmin(User))
            return await _db.Departments.AnyAsync(d => d.Id == departmentId.Value);

        var tenantIds = await _tenantContext.GetAuthorizedTenantIdsAsync(User);
        return await _db.Departments.AnyAsync(d => d.Id == departmentId.Value
            && d.TenantId.HasValue && tenantIds.Contains(d.TenantId.Value));
    }

    [HttpGet]
    public async Task<ActionResult<List<CodeCallLocation>>> GetAll([FromQuery] bool includeInactive = false)
    {
        // Filtering unconditionally is the point: skipping it when the user resolves to no
        // tenants used to fail OPEN.
        var query = await ScopedAsync(_db.CodeCallLocations);

        if (!includeInactive)
            query = query.Where(l => l.IsActive);
        return await query.OrderBy(l => l.Name).ToListAsync();
    }

    [HttpGet("{id}")]
    public async Task<ActionResult<CodeCallLocation>> Get(int id)
    {
        var query = await ScopedAsync(_db.CodeCallLocations.Where(l => l.Id == id));
        var location = await query.FirstOrDefaultAsync();
        if (location == null) return NotFound();
        return location;
    }

    [HttpPost]
    [Authorize(Policy = "RequireCodeCallWrite")]
    public async Task<ActionResult<CodeCallLocation>> Create(CodeCallLocation location)
    {
        if (!await DepartmentAllowedAsync(location.DepartmentId)) return NotFound();

        _db.CodeCallLocations.Add(location);
        await _db.SaveChangesAsync();
        return CreatedAtAction(nameof(Get), new { id = location.Id }, location);
    }

    [HttpPut("{id}")]
    [Authorize(Policy = "RequireCodeCallWrite")]
    public async Task<ActionResult<CodeCallLocation>> Update(int id, CodeCallLocation location)
    {
        if (id != location.Id) return BadRequest();

        var scoped = await ScopedAsync(_db.CodeCallLocations.Where(l => l.Id == id));
        var existing = await scoped.FirstOrDefaultAsync();
        if (existing == null) return NotFound();

        // Guard the destination too, so an update cannot move a location into another tenant.
        if (!await DepartmentAllowedAsync(location.DepartmentId)) return NotFound();

        existing.Name = location.Name;
        existing.Zone = location.Zone;
        existing.DepartmentId = location.DepartmentId;
        existing.IsActive = location.IsActive;
        await _db.SaveChangesAsync();
        return Ok(existing);
    }

    [HttpDelete("{id}")]
    [Authorize(Policy = "RequireAdminFull")]
    public async Task<ActionResult> Delete(int id)
    {
        var scoped = await ScopedAsync(_db.CodeCallLocations.Where(l => l.Id == id));
        var location = await scoped.FirstOrDefaultAsync();
        if (location == null) return NotFound();
        location.IsActive = false;
        await _db.SaveChangesAsync();
        return NoContent();
    }
}
