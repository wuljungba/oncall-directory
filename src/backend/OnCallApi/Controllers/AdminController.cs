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

    // ── Bulk employee actions ──
    //
    // POST rather than DELETE for the delete route: the id list is a request body, and
    // DELETE-with-body is unreliable through proxies.
    //
    // Same policy as the single-record actions. Tightening bulk to Admin.Full would take it
    // away from exactly the scoped administrators who onboard and clean up their own
    // subscription, which is the case it exists for.
    //
    // These answer 200 with a per-record report rather than a single status. A batch where some
    // records are blocked by schedule history is the ordinary outcome, not an error, and one
    // status code cannot say which of five hundred people it refers to.

    /// <summary>Deactivate many employees, revoking their permission grants.</summary>
    [HttpPost("employees/bulk/deactivate")]
    [Authorize(Policy = "RequireDirectoryWrite")]
    public Task<ActionResult<BulkEmployeeActionResponse>> BulkDeactivate(BulkEmployeeActionRequest request)
        => RunBulk(request, "EmployeeDeactivated",
            (ids, reason, ack) => _adminService.BulkDeactivateEmployeesAsync(ids, reason, ack));

    /// <summary>Reactivate many employees. Does not restore permissions.</summary>
    [HttpPost("employees/bulk/reactivate")]
    [Authorize(Policy = "RequireDirectoryWrite")]
    public Task<ActionResult<BulkEmployeeActionResponse>> BulkReactivate(BulkEmployeeActionRequest request)
        => RunBulk(request, "EmployeeUpdated",
            (ids, reason, ack) => _adminService.BulkReactivateEmployeesAsync(ids, reason, ack));

    /// <summary>Permanently delete many employees, revoking their permission grants.</summary>
    [HttpPost("employees/bulk/delete")]
    [Authorize(Policy = "RequireDirectoryWrite")]
    public Task<ActionResult<BulkEmployeeActionResponse>> BulkDelete(BulkEmployeeActionRequest request)
        => RunBulk(request, "EmployeeDeleted",
            (ids, reason, ack) => _adminService.BulkDeleteEmployeesAsync(ids, reason, ack));

    private async Task<ActionResult<BulkEmployeeActionResponse>> RunBulk(
        BulkEmployeeActionRequest request,
        string broadcastEvent,
        Func<IReadOnlyList<Guid>, string?, bool, Task<BulkEmployeeActionResponse>> run)
    {
        if (request.EmployeeIds is not { Count: > 0 })
            return BadRequest(new { error = "Select at least one record." });

        // Checked here, not only in the service: the loop below costs one query per id, so an
        // over-sized list would buy thousands of round trips for a call that was always going to
        // be refused.
        if (request.EmployeeIds.Count > BulkLimits.MaxBatch)
            return BadRequest(new { error = $"A bulk action takes at most {BulkLimits.MaxBatch} records at a time." });

        // Resolved before the action, while the rows that carry the tenant still exist.
        var tenantByEmployee = new Dictionary<Guid, int?>();
        foreach (var id in request.EmployeeIds.Distinct())
            tenantByEmployee[id] = await _broadcast.TenantForEmployeeAsync(id);

        BulkEmployeeActionResponse result;
        try
        {
            result = await run(request.EmployeeIds, request.Reason, request.AcknowledgePrivileged);
        }
        catch (ArgumentOutOfRangeException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            // The batch contains an administrator whose rights this will not remove, and the
            // caller has not confirmed they have seen that.
            return BadRequest(new { error = ex.Message, requiresAcknowledgement = true });
        }
        catch (UnauthorizedAccessException)
        {
            return Forbid();
        }

        // One event per affected subscription, not per record. Every one of these raises a toast
        // and schedules a dashboard refetch on every connected client, so a per-record broadcast
        // meant a five-hundred-record batch stacked five hundred toasts on everyone.
        var affectedTenants = result.Results
            .Where(r => r.Outcome == BulkOutcomes.Succeeded)
            .Select(r => tenantByEmployee.GetValueOrDefault(r.EmployeeId))
            .Distinct()
            .ToList();

        foreach (var tenant in affectedTenants)
        {
            var ids = result.Results
                .Where(r => r.Outcome == BulkOutcomes.Succeeded
                    && tenantByEmployee.GetValueOrDefault(r.EmployeeId) == tenant)
                .Select(r => r.EmployeeId)
                .ToList();

            await _broadcast.ToTenantAsync(tenant, broadcastEvent, new { ids, count = ids.Count });
        }

        return Ok(result);
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
