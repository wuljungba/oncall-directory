using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using OnCallApi.Hubs;
using OnCallApi.Models;
using OnCallApi.Services;

namespace OnCallApi.Controllers;

[ApiController]
[Route("api/admin")]
public class AdminController : ControllerBase
{
    private readonly IAdminService _adminService;
    private readonly ILogger<AdminController> _logger;
    private readonly ITenantBroadcaster _broadcast;

    public AdminController(IAdminService adminService, ILogger<AdminController> logger, ITenantBroadcaster broadcast)
    {
        _adminService = adminService;
        _logger = logger;
        _broadcast = broadcast;
    }

    // ── Employees ──

    /// <summary>List all employees, optionally including inactive ones.</summary>
    [HttpGet("employees")]
    [Authorize(Policy = "RequireDirectoryRead")]
    public async Task<ActionResult<List<Employee>>> GetAllEmployees([FromQuery] bool includeInactive = false)
    {
        return await _adminService.GetAllEmployeesAsync(includeInactive);
    }

    /// <summary>Get a single employee by ID with direct reports.</summary>
    [HttpGet("employees/{id}")]
    [Authorize(Policy = "RequireDirectoryRead")]
    public async Task<ActionResult<Employee>> GetEmployee(Guid id)
    {
        var employee = await _adminService.GetEmployeeByIdAsync(id);
        if (employee == null) return NotFound();
        return employee;
    }

    /// <summary>Create a new employee account.</summary>
    [HttpPost("employees")]
    [Authorize(Policy = "RequireDirectoryWrite")]
    public async Task<ActionResult<Employee>> CreateEmployee([FromBody] CreateEmployeeRequest request)
    {
        try
        {
            var employee = await _adminService.CreateEmployeeAsync(request);
            await _broadcast.ToTenantAsync(employee.TenantId, "EmployeeCreated", employee);
            return CreatedAtAction(nameof(GetEmployee), new { id = employee.Id }, employee);
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { error = ex.Message });
        }
    }

    /// <summary>Update an existing employee account.</summary>
    [HttpPut("employees/{id}")]
    [Authorize(Policy = "RequireDirectoryWrite")]
    public async Task<ActionResult<Employee>> UpdateEmployee(Guid id, [FromBody] UpdateEmployeeRequest request)
    {
        try
        {
            var employee = await _adminService.UpdateEmployeeAsync(id, request);
            await _broadcast.ToTenantAsync(employee.TenantId, "EmployeeUpdated", employee);
            return Ok(employee);
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { error = ex.Message });
        }
    }

    /// <summary>Soft-delete (deactivate) an employee account.</summary>
    [HttpDelete("employees/{id}")]
    [Authorize(Policy = "RequireDirectoryWrite")]
    public async Task<ActionResult> DeactivateEmployee(Guid id)
    {
        try
        {
            var deactivatedTenantId = await _broadcast.TenantForEmployeeAsync(id);
            await _adminService.DeactivateEmployeeAsync(id);
            await _broadcast.ToTenantAsync(deactivatedTenantId, "EmployeeDeactivated", new { id });
            return NoContent();
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
    }

    /// <summary>Reactivate a previously deactivated employee.</summary>
    [HttpPost("employees/{id}/reactivate")]
    [Authorize(Policy = "RequireDirectoryWrite")]
    public async Task<ActionResult> ReactivateEmployee(Guid id)
    {
        try
        {
            await _adminService.ReactivateEmployeeAsync(id);
            await _broadcast.ToTenantAsync(
                await _broadcast.TenantForEmployeeAsync(id), "EmployeeUpdated", new { id, isActive = true });
            return NoContent();
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
    }

    /// <summary>Permanently delete an employee account (tenant-guarded). Fails with a
    /// conflict if the employee is still referenced by schedule/time-off/phone-tree rows.</summary>
    [HttpDelete("employees/{id}/hard-delete")]
    [Authorize(Policy = "RequireDirectoryWrite")]
    public async Task<ActionResult> DeleteEmployee(Guid id)
    {
        try
        {
            // Resolved before the delete, while the row that carries the tenant still exists.
            var deletedTenantId = await _broadcast.TenantForEmployeeAsync(id);
            await _adminService.DeleteEmployeeAsync(id);
            await _broadcast.ToTenantAsync(deletedTenantId, "EmployeeDeleted", new { id });
            return NoContent();
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { error = ex.Message });
        }
    }

    /// <summary>Get direct reports for a manager.</summary>
    [HttpGet("employees/{id}/direct-reports")]
    [Authorize(Policy = "RequireDirectoryRead")]
    public async Task<ActionResult<List<Employee>>> GetDirectReports(Guid id)
    {
        return await _adminService.GetDirectReportsAsync(id);
    }

    // ── Departments ──

    /// <summary>List all departments, optionally including inactive ones.</summary>
    [HttpGet("departments")]
    [Authorize(Policy = "RequireDirectoryRead")]
    public async Task<ActionResult<List<Department>>> GetAllDepartments([FromQuery] bool includeInactive = false)
    {
        return await _adminService.GetAllDepartmentsAsync(includeInactive);
    }

    /// <summary>Create a new department (sub-account).</summary>
    [HttpPost("departments")]
    [Authorize(Policy = "RequireAdminFullOrScoped")]
    public async Task<ActionResult<Department>> CreateDepartment([FromBody] CreateDepartmentRequest request)
    {
        try
        {
            var department = await _adminService.CreateDepartmentAsync(request);
            await _broadcast.ToTenantAsync(department.TenantId, "DepartmentCreated", department);
            return CreatedAtAction(nameof(GetAllDepartments), new { id = department.Id }, department);
        }
        catch (InvalidOperationException ex)
        {
            // Without this the action had no handler at all, so a name clash or a bad
            // tenant surfaced as an opaque 500.
            return Conflict(new { error = ex.Message });
        }
    }

    /// <summary>Update a department.</summary>
    [HttpPut("departments/{id}")]
    [Authorize(Policy = "RequireAdminFullOrScoped")]
    public async Task<ActionResult<Department>> UpdateDepartment(int id, [FromBody] UpdateDepartmentRequest request)
    {
        try
        {
            var department = await _adminService.UpdateDepartmentAsync(id, request);
            await _broadcast.ToTenantAsync(department.TenantId, "DepartmentUpdated", department);
            return Ok(department);
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
    }

    /// <summary>Deactivate a department.</summary>
    [HttpDelete("departments/{id}")]
    [Authorize(Policy = "RequireAdminFullOrScoped")]
    public async Task<ActionResult> DeactivateDepartment(int id)
    {
        try
        {
            var deptTenantId = await _broadcast.TenantForDepartmentAsync(id);
            await _adminService.DeactivateDepartmentAsync(id);
            await _broadcast.ToTenantAsync(deptTenantId, "DepartmentDeactivated", new { id });
            return NoContent();
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
    }

    /// <summary>Get all active employees in a department.</summary>
    [HttpGet("departments/{id}/members")]
    [Authorize(Policy = "RequireDirectoryRead")]
    public async Task<ActionResult<List<Employee>>> GetDepartmentMembers(int id)
    {
        return await _adminService.GetDepartmentMembersAsync(id);
    }
}
