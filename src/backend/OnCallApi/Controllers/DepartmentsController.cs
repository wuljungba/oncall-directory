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

    /// <summary>
    /// Restricts a department query to the caller's tenants.
    ///
    /// Shared by the list and single-item endpoints deliberately: the filter lived only in
    /// GetAll, so Get(id) returned any tenant's department to anyone holding Directory.Read
    /// — a plain id-enumeration hole. Keeping one helper stops the two drifting again.
    /// </summary>
    private async Task<IQueryable<Department>> ScopedAsync(IQueryable<Department> query)
    {
        if (_tenantContext.IsSuperAdmin(User)) return query;

        var tenantIds = await _tenantContext.GetAuthorizedTenantIdsAsync(User);
        return query.Where(d => d.TenantId.HasValue && tenantIds.Contains(d.TenantId.Value));
    }

    [HttpGet]
    public async Task<ActionResult<List<Department>>> GetAll()
    {
        // Filtering unconditionally is the point: skipping it when the user resolves to no
        // tenants used to fail OPEN and show them every tenant's departments.
        var query = await ScopedAsync(_db.Departments.Where(d => d.IsActive));
        return await query.OrderBy(d => d.Name).ToListAsync();
    }

    [HttpGet("{id}")]
    public async Task<ActionResult<Department>> Get(int id)
    {
        var query = await ScopedAsync(_db.Departments.Where(d => d.Id == id));
        var dept = await query.FirstOrDefaultAsync();
        // Not found rather than forbidden: the endpoint must not confirm that another
        // tenant's department exists.
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
