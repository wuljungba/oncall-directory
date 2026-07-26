using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OnCallApi.Data;
using OnCallApi.Models;

namespace OnCallApi.Controllers;

[ApiController]
[Route("api/tenants/{tenantId}/admins")]
[Authorize(Policy = "RequireTenantManage")]
public class TenantAdminsController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly ILogger<TenantAdminsController> _logger;

    public TenantAdminsController(AppDbContext db, ILogger<TenantAdminsController> logger)
    {
        _db = db;
        _logger = logger;
    }

    /// <summary>List all admins assigned to a tenant.</summary>
    [HttpGet]
    public async Task<ActionResult<List<TenantAdmin>>> GetAll(int tenantId)
    {
        var tenantExists = await _db.Tenants.AnyAsync(t => t.Id == tenantId);
        if (!tenantExists) return NotFound();

        return await _db.TenantAdmins
            .Where(a => a.TenantId == tenantId)
            .OrderBy(a => a.AzureAdObjectId)
            .ToListAsync();
    }

    /// <summary>Assign a user as a tenant admin.</summary>
    [HttpPost]
    public async Task<ActionResult<TenantAdmin>> Assign(int tenantId, [FromBody] AssignTenantAdminRequest request)
    {
        var tenantExists = await _db.Tenants.AnyAsync(t => t.Id == tenantId);
        if (!tenantExists) return NotFound();

        // Check for duplicate assignment
        var existing = await _db.TenantAdmins
            .AnyAsync(a => a.TenantId == tenantId && a.AzureAdObjectId == request.AzureAdObjectId);
        if (existing)
            return Conflict(new { error = "This user is already assigned as an admin for this tenant." });

        // Validate role
        if (request.Role != "DepartmentAdmin" && request.Role != "SuperAdmin")
            return BadRequest(new { error = "Role must be 'DepartmentAdmin' or 'SuperAdmin'." });

        var admin = new TenantAdmin
        {
            TenantId = tenantId,
            AzureAdObjectId = request.AzureAdObjectId,
            Role = request.Role,
            IsAutoAssigned = false,
            CreatedAt = DateTime.UtcNow,
        };

        _db.TenantAdmins.Add(admin);
        await _db.SaveChangesAsync();
        _logger.LogInformation("Tenant admin assigned: Tenant={TenantId}, User={AzureAdObjectId}, Role={Role}",
            tenantId, request.AzureAdObjectId, request.Role);

        return CreatedAtAction(nameof(GetAll), new { tenantId }, admin);
    }

    /// <summary>Update a tenant admin's role.</summary>
    [HttpPut("{id}")]
    public async Task<ActionResult<TenantAdmin>> Update(int tenantId, int id, [FromBody] UpdateTenantAdminRequest request)
    {
        var admin = await _db.TenantAdmins
            .FirstOrDefaultAsync(a => a.Id == id && a.TenantId == tenantId);
        if (admin == null) return NotFound();

        if (request.Role != null)
        {
            if (request.Role != "DepartmentAdmin" && request.Role != "SuperAdmin")
                return BadRequest(new { error = "Role must be 'DepartmentAdmin' or 'SuperAdmin'." });
            admin.Role = request.Role;
        }

        await _db.SaveChangesAsync();
        _logger.LogInformation("Tenant admin updated: Id={Id}, Tenant={TenantId}, Role={Role}",
            id, tenantId, admin.Role);

        return Ok(admin);
    }

    /// <summary>Remove a tenant admin assignment.</summary>
    [HttpDelete("{id}")]
    public async Task<ActionResult> Remove(int tenantId, int id)
    {
        var admin = await _db.TenantAdmins
            .FirstOrDefaultAsync(a => a.Id == id && a.TenantId == tenantId);
        if (admin == null) return NotFound();

        _db.TenantAdmins.Remove(admin);
        await _db.SaveChangesAsync();
        _logger.LogInformation("Tenant admin removed: Id={Id}, Tenant={TenantId}, User={AzureAdObjectId}",
            id, tenantId, admin.AzureAdObjectId);

        return NoContent();
    }
}
