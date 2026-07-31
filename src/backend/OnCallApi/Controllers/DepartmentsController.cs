using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OnCallApi.Data;
using OnCallApi.Models;
using OnCallApi.Services;

namespace OnCallApi.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize(Policy = "RequireDirectoryRead")]
public class DepartmentsController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly ITenantContextService _tenantContext;

    public DepartmentsController(AppDbContext db, ITenantContextService tenantContext)
    {
        _db = db;
        _tenantContext = tenantContext;
    }

    [HttpGet]
    public async Task<ActionResult<List<Department>>> GetAll()
    {
        var query = _db.Departments.Where(d => d.IsActive).AsQueryable();

        // Apply tenant scoping for non-super-admin users
        if (!_tenantContext.IsSuperAdmin(User))
        {
            var tenantIds = await _tenantContext.GetAuthorizedTenantIdsAsync(User);
            if (tenantIds.Count > 0)
            {
                query = query.Where(d => d.TenantId.HasValue && tenantIds.Contains(d.TenantId.Value));
            }
        }

        return await query.OrderBy(d => d.Name).ToListAsync();
    }

    [HttpGet("{id}")]
    public async Task<ActionResult<Department>> Get(int id)
    {
        var dept = await _db.Departments.FindAsync(id);
        if (dept == null) return NotFound();
        return dept;
    }

    [HttpPost]
    [Authorize(Policy = "RequireAdminFull")]
    public async Task<ActionResult<Department>> Create(Department department)
    {
        _db.Departments.Add(department);
        await _db.SaveChangesAsync();
        return CreatedAtAction(nameof(Get), new { id = department.Id }, department);
    }
}
