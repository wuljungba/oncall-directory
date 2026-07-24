using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OnCallApi.Data;
using OnCallApi.Models;

namespace OnCallApi.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize(Policy = "RequireDirectoryRead")]
public class DepartmentsController : ControllerBase
{
    private readonly AppDbContext _db;

    public DepartmentsController(AppDbContext db) => _db = db;

    [HttpGet]
    public async Task<ActionResult<List<Department>>> GetAll()
    {
        return await _db.Departments.Where(d => d.IsActive).OrderBy(d => d.Name).ToListAsync();
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
