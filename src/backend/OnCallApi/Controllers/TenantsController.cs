using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OnCallApi.Data;
using OnCallApi.Models;

namespace OnCallApi.Controllers;

[ApiController]
[Route("api/tenants")]
[Authorize(Policy = "RequireTenantManage")]
public class TenantsController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly ILogger<TenantsController> _logger;

    public TenantsController(AppDbContext db, ILogger<TenantsController> logger)
    {
        _db = db;
        _logger = logger;
    }

    /// <summary>List all tenants.</summary>
    [HttpGet]
    public async Task<ActionResult<List<Tenant>>> GetAll([FromQuery] bool includeInactive = false)
    {
        var query = _db.Tenants.AsQueryable();
        if (!includeInactive)
            query = query.Where(t => t.IsActive);
        return await query.OrderBy(t => t.Name).ToListAsync();
    }

    /// <summary>Get a single tenant by ID.</summary>
    [HttpGet("{id}")]
    public async Task<ActionResult<Tenant>> Get(int id)
    {
        var tenant = await _db.Tenants.FindAsync(id);
        if (tenant == null) return NotFound();
        return tenant;
    }

    /// <summary>Create a new tenant (business/facility).</summary>
    [HttpPost]
    public async Task<ActionResult<Tenant>> Create([FromBody] CreateTenantRequest request)
    {
        // Trimmed and compared case-insensitively: "Acme" and "acme " both succeeded and
        // then looked identical in the admin UI, which is a support call waiting to happen.
        var name = request.Name.Trim();
        var existing = await _db.Tenants.AnyAsync(t => t.Name.ToLower() == name.ToLower());
        if (existing)
            return Conflict(new { error = "A tenant with this name already exists." });

        var tenant = new Tenant
        {
            Name = name,
            Description = request.Description,
            AzureAdGroupId = request.AzureAdGroupId,
            ContactEmail = request.ContactEmail,
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
        };

        _db.Tenants.Add(tenant);
        await _db.SaveChangesAsync();
        _logger.LogInformation("Tenant created: {TenantId} {Name}", tenant.Id, tenant.Name);

        return CreatedAtAction(nameof(Get), new { id = tenant.Id }, tenant);
    }

    /// <summary>Update a tenant.</summary>
    [HttpPut("{id}")]
    public async Task<ActionResult<Tenant>> Update(int id, [FromBody] UpdateTenantRequest request)
    {
        var tenant = await _db.Tenants.FindAsync(id);
        if (tenant == null) return NotFound();

        if (request.Name != null) tenant.Name = request.Name;
        if (request.Description != null) tenant.Description = request.Description;
        if (request.AzureAdGroupId != null) tenant.AzureAdGroupId = request.AzureAdGroupId;
        if (request.ContactEmail != null) tenant.ContactEmail = request.ContactEmail;
        if (request.IsActive.HasValue) tenant.IsActive = request.IsActive.Value;

        await _db.SaveChangesAsync();
        _logger.LogInformation("Tenant updated: {TenantId}", id);

        return Ok(tenant);
    }

    /// <summary>Deactivate a tenant (soft delete).</summary>
    [HttpDelete("{id}")]
    public async Task<ActionResult> Deactivate(int id)
    {
        var tenant = await _db.Tenants.FindAsync(id);
        if (tenant == null) return NotFound();

        tenant.IsActive = false;
        await _db.SaveChangesAsync();
        _logger.LogInformation("Tenant deactivated: {TenantId}", id);

        return NoContent();
    }
}
