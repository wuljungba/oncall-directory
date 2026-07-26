using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OnCallApi.Data;
using OnCallApi.Models;
using OnCallApi.Services;

namespace OnCallApi.Controllers;

[ApiController]
[Route("api/code-call-locations")]
public class CodeCallLocationsController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly ITenantContextService _tenantContext;

    public CodeCallLocationsController(AppDbContext db, ITenantContextService tenantContext)
    {
        _db = db;
        _tenantContext = tenantContext;
    }

    [HttpGet]
    public async Task<ActionResult<List<CodeCallLocation>>> GetAll([FromQuery] bool includeInactive = false)
    {
        var query = _db.CodeCallLocations.AsQueryable();

        // Apply tenant scoping for non-super-admin users
        if (!_tenantContext.IsSuperAdmin(User))
        {
            var tenantIds = await _tenantContext.GetAuthorizedTenantIdsAsync(User);
            if (tenantIds.Count > 0)
            {
                query = query.Where(l => l.Department == null ||
                    (l.Department.TenantId.HasValue && tenantIds.Contains(l.Department.TenantId.Value)));
            }
        }

        if (!includeInactive)
            query = query.Where(l => l.IsActive);
        return await query.OrderBy(l => l.Name).ToListAsync();
    }

    [HttpGet("{id}")]
    public async Task<ActionResult<CodeCallLocation>> Get(int id)
    {
        var location = await _db.CodeCallLocations.FindAsync(id);
        if (location == null) return NotFound();
        return location;
    }

    [HttpPost]
    [Authorize(Policy = "RequireCodeCallWrite")]
    public async Task<ActionResult<CodeCallLocation>> Create(CodeCallLocation location)
    {
        _db.CodeCallLocations.Add(location);
        await _db.SaveChangesAsync();
        return CreatedAtAction(nameof(Get), new { id = location.Id }, location);
    }

    [HttpPut("{id}")]
    [Authorize(Policy = "RequireCodeCallWrite")]
    public async Task<ActionResult<CodeCallLocation>> Update(int id, CodeCallLocation location)
    {
        if (id != location.Id) return BadRequest();
        var existing = await _db.CodeCallLocations.FindAsync(id)
            ?? throw new KeyNotFoundException($"Location {id} not found");
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
        var location = await _db.CodeCallLocations.FindAsync(id);
        if (location == null) return NotFound();
        location.IsActive = false;
        await _db.SaveChangesAsync();
        return NoContent();
    }
}
